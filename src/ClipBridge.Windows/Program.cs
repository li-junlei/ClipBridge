using ClipBridge;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        using var instance = new Mutex(true, @"Local\ClipBridge-" + System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value, out bool first);
        if (!first) { MessageBox.Show("ClipBridge 已在运行。"); return; }
        using var installGuard = new Mutex(false, @"Local\ClipBridge.Install");
        Application.Run(new BridgeWindow());
    }
}

sealed partial class BridgeWindow : Form
{
    private readonly string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipBridge");
    private readonly NotifyIcon tray;
    private readonly CancellationTokenSource stop = new();
    private readonly ToolStripMenuItem status = new("尚未配置");
    private ClientConfig? config;
    private RelayClient? relay;
    private SyncState state = new();
    private string? lastObserved;
    private bool paused;
    private bool started;
    private CancellationTokenSource? connectionStop;
    private bool exiting;
    private string peerName = "";
    private long lastSync;
    private bool startupLaunch = Environment.GetCommandLineArgs().Contains("--startup");
    private Icon appIcon = new(typeof(BridgeWindow).Assembly.GetManifestResourceStream("ClipBridge.App.ico")!);
    private Icon trayIcon = new(typeof(BridgeWindow).Assembly.GetManifestResourceStream("ClipBridge.Tray.ico")!);
    public BridgeWindow()
    {
        Text = "ClipBridge"; Icon = appIcon;
        var menu = new ContextMenuStrip(); menu.Items.Add(status);
        menu.Items.Add("打开 ClipBridge", null, (_, _) => OpenDashboard());
        menu.Items.Add("导入配对文件…", null, (_, _) => Import());
        var pause = new ToolStripMenuItem("暂停同步") { CheckOnClick = true };
        pause.CheckedChanged += (_, _) => { paused = pause.Checked; status.Text = paused ? "已暂停" : "同步中"; };
        menu.Items.Add(pause);
        menu.Items.Add("退出", null, (_, _) => { exiting = true; Close(); });
        tray = new NotifyIcon { Icon = trayIcon, Text = "ClipBridge", ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += (_, _) => OpenDashboard();
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "config.bin");
            if (File.Exists(path))
            {
                config = JsonSerializer.Deserialize<ClientConfig>(ProtectedFile.Read(path), Wire.Json)!; config.Validate();
                var sp = Path.Combine(folder, "state.bin");
                // Missing replay state must not silently reset an existing pairing.
                state = JsonSerializer.Deserialize<SyncState>(ProtectedFile.Read(sp), Wire.Json)!;
                state.Validate(config);
                status.Text = "正在连接服务器";
            }
        }
        catch { config = null; status.Text = "配置或状态损坏，请重新配对"; }
        if (config is not null) LoadDetails();
        BuildDashboard();
    }
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (!AddClipboardFormatListener(Handle)) throw new Win32Exception();
    }
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e); started = true;
        StartSync();
        if(startupLaunch) Hide();
    }
    private void Import()
    {
        if (config is not null || File.Exists(Path.Combine(folder, "config.bin")))
        { MessageBox.Show("请先在设备页删除当前配置，再导入新配置。"); return; }
        try
        {
            using var dialog = new OpenFileDialog
            {
                Title = "导入 ClipBridge 配对文件",
                Filter = "ClipBridge 配对文件 (*.json)|*.json",
                CheckFileExists = true,
                RestoreDirectory = true
            };
            // The tray form stays hidden/minimized; do not use it as a modal owner.
            if (dialog.ShowDialog() != DialogResult.OK) return;
            if (new FileInfo(dialog.FileName).Length > 8192) throw new InvalidDataException();
            var next = JsonSerializer.Deserialize<ClientConfig>(File.ReadAllText(dialog.FileName), Wire.Json)!; next.Validate();
            UsePairing(next);
            status.Text = "配对已导入，正在连接";
            MessageBox.Show("配对已导入并由当前 Windows 用户加密保存。请安全删除原始配对文件；不要上传或分享。");
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or FormatException or InvalidDataException)
        {
            status.Text = "配对文件无效";
            MessageBox.Show("配对文件无效。请选择 windows.json，并检查服务器地址为 HTTPS。", "ClipBridge", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            status.Text = "导入失败";
            // Exception messages may contain configuration values; show only a non-sensitive error type.
            MessageBox.Show($"无法打开或导入配对文件（{ex.GetType().Name}）。请检查文件访问权限后重试。", "ClipBridge", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
    private void SaveState()
    {
        try { ProtectedFile.Write(Path.Combine(folder, "state.bin"), JsonSerializer.SerializeToUtf8Bytes(state, Wire.Json)); }
        catch { paused = true; throw; } // Fail closed if replay state cannot be persisted.
    }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x031D && !paused && config is not null)
        {
            try
            {
                if (Clipboard.ContainsText())
                {
                    string text = Clipboard.GetText();
                    if (text != lastObserved)
                    {
                        state.Queue(config, text); SaveState(); lastObserved = text;
                    }
                }
            }
            catch { status.Text = "读取剪贴板或保存失败"; }
        }
        base.WndProc(ref m);
    }
    private void StartSync()
    {
        connectionStop?.Cancel(); relay?.Dispose();
        if (!started || config is null) return;
        connectionStop = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        relay = new RelayClient(config);
        _ = NameLoop(relay, config, connectionStop.Token);
        _ = SendLoop(relay, connectionStop.Token); _ = ReceiveLoop(relay, config, connectionStop.Token);
    }
    private void UsePairing(ClientConfig next)
    {
        next.Validate();
        if (config == next) return;
        var oldConfig = config; var oldState = state;
        connectionStop?.Cancel();
        try
        {
            if (oldConfig is not null)
            {
                string archive = Path.Combine(folder, "profiles", oldConfig.Pair); Directory.CreateDirectory(archive);
                ProtectedFile.Write(Path.Combine(archive, "config.bin"), JsonSerializer.SerializeToUtf8Bytes(oldConfig, Wire.Json));
                ProtectedFile.Write(Path.Combine(archive, "state.bin"), JsonSerializer.SerializeToUtf8Bytes(oldState, Wire.Json));
            }
            var replay = Path.Combine(folder,"profiles",next.Pair,"state.bin");
            var newState = File.Exists(replay) ? JsonSerializer.Deserialize<SyncState>(ProtectedFile.Read(replay),Wire.Json)! : new SyncState();
            newState.Validate(next); newState.Pending=null;
            ProtectedFile.Write(Path.Combine(folder, "state.bin"), JsonSerializer.SerializeToUtf8Bytes(newState, Wire.Json));
            ProtectedFile.Write(Path.Combine(folder, "config.bin"), JsonSerializer.SerializeToUtf8Bytes(next, Wire.Json));
            config = next; state = newState; paused = false; lastObserved = null; peerName=""; lastSync=0; SaveDetails();
            status.Text = "已配对，正在连接"; StartSync(); RenderPage();
        }
        catch
        {
            if (oldConfig is not null)
            {
                ProtectedFile.Write(Path.Combine(folder, "state.bin"), JsonSerializer.SerializeToUtf8Bytes(oldState, Wire.Json));
                ProtectedFile.Write(Path.Combine(folder, "config.bin"), JsonSerializer.SerializeToUtf8Bytes(oldConfig, Wire.Json));
            }
            config = oldConfig; state = oldState; StartSync(); throw;
        }
    }
    private async Task SendLoop(RelayClient current, CancellationToken ct)
    {
        int delay = 1;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!paused && state.Pending is { } pending)
                {
                    SaveState();
                    await current.Send(pending, ct); ct.ThrowIfCancellationRequested();
                    if (state.Pending?.Id == pending.Id) { state.Pending = null; SaveState(); }
                    MarkSynced(); status.Text = "已发送";
                }
                delay = 1;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch { status.Text = "发送失败，等待重试"; delay = Math.Min(delay * 2, 60); }
            try { await Task.Delay(TimeSpan.FromSeconds(delay), ct); } catch (OperationCanceledException) { break; }
        }
    }
    private async Task ReceiveLoop(RelayClient current, ClientConfig active, CancellationToken ct)
    {
        string after = ""; int delay = 1;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (paused) { await Task.Delay(1000, ct); continue; }
                var e = await current.Receive(after, ct); ct.ThrowIfCancellationRequested();
                if (e is null) status.Text = "已连接，等待新内容";
                if (e is not null)
                {
                    if (paused) continue;
                    after = e.Id;
                    var p = state.Accept(active, e);
                    if (p is not null)
                    {
                        SaveState();
                        // Retry clipboard contention without blocking the UI or overwriting newer local copies.
                        for (int attempt = 0; attempt < 10 && state.Clock == e.Clock && state.Winner == e.From; attempt++)
                        {
                            try
                            {
                                if (paused) break;
                                lastObserved = p.Text;
                                if (p.Text.Length == 0) Clipboard.Clear(); else Clipboard.SetText(p.Text);
                                MarkSynced(); status.Text = "已接收"; break;
                            }
                            catch (ExternalException) { status.Text = "剪贴板忙，正在重试"; await Task.Delay(100, ct); ct.ThrowIfCancellationRequested(); }
                        }
                    }
                }
                delay = 1;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (CryptographicException) { status.Text = "已拒绝过期或认证失败的消息"; }
            catch { status.Text = "连接失败，等待重连"; delay = Math.Min(delay * 2, 60); }
            try { await Task.Delay(TimeSpan.FromSeconds(delay), ct); } catch (OperationCanceledException) { break; }
        }
    }
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!exiting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); return; }
        base.OnFormClosing(e);
    }
    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        uiTimer.Stop();uiTimer.Dispose();stop.Cancel(); connectionStop?.Cancel(); RemoveClipboardFormatListener(Handle); tray.Dispose(); appIcon.Dispose(); trayIcon.Dispose(); relay?.Dispose(); base.OnFormClosed(e);
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
}
static class ProtectedFile
{
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr ptr);
    private static byte[] Transform(byte[] bytes, bool protect)
    {
        var input = new Blob { Size = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) }; Blob output = default;
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            bool ok = protect ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new Win32Exception();
            var result = new byte[output.Size]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        finally { Marshal.FreeHGlobal(input.Data); if (output.Data != IntPtr.Zero) LocalFree(output.Data); }
    }
    public static byte[] Read(string path) => Transform(File.ReadAllBytes(path), false);
    public static void Write(string path, byte[] plain)
    {
        try
        {
            using (var file = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
            { file.Write(Transform(plain, true)); file.Flush(true); }
            File.Move(path + ".tmp", path, true);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
}
