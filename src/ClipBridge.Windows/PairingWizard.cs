using ClipBridge;
using QRCoder;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

sealed class PairingWizard : Form
{
    private readonly TextBox server = new() { PlaceholderText = "https://你的服务器:端口", BorderStyle = BorderStyle.FixedSingle };
    private readonly TextBox code = new() { PlaceholderText = "从服务器日志中复制", UseSystemPasswordChar = true, BorderStyle = BorderStyle.FixedSingle };
    private readonly Button generate = BridgeWindow.MakeButton("生成配对二维码", true);
    private readonly Button finish = BridgeWindow.MakeButton("完成并启用此配对", true);
    private readonly Label feedback = BridgeWindow.MakeLabel("", 10);
    private readonly PictureBox qrImage = new() { SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.White };
    private readonly Label countdown = BridgeWindow.MakeLabel("", 10);
    private readonly string pendingPath;
    private readonly CancellationTokenSource lifetime = new();
    private readonly System.Windows.Forms.Timer poll = new() { Interval = 2000 };
    private PendingInvite? pending;
    private bool polling;
    public ClientConfig? SelectedConfig { get; private set; }
    private sealed record PendingInvite(ClientConfig Windows, InviteEnvelope Envelope, string Qr);
    public PairingWizard(string? currentServer, string folder, bool hasExisting)
    {
        Text = "添加配对 · ClipBridge"; StartPosition = FormStartPosition.CenterParent; FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false; ClientSize = new Size(720, 650); BackColor = Color.FromArgb(246, 247, 250); Font = new Font("Microsoft YaHei UI", 10);
        pendingPath = Path.Combine(folder, "pending-pairing.bin");
        var title = BridgeWindow.MakeLabel("连接你的手机", 23, FontStyle.Bold); title.Location = new Point(30, 25); Controls.Add(title);
        var sub = BridgeWindow.MakeLabel(hasExisting ? "现有连接会保留，扫码完成并确认后再切换。" : "填写一次服务器信息，手机扫一扫就能连接。", 10); sub.Location = new Point(32, 75); sub.ForeColor = Color.Gray; Controls.Add(sub);
        var a = BridgeWindow.MakeLabel("服务器地址", 10, FontStyle.Bold); a.Location = new Point(32, 125); Controls.Add(a);
        server.SetBounds(32, 156, 326, 34); server.Text = currentServer ?? ""; Controls.Add(server);
        var b = BridgeWindow.MakeLabel("服务器配对管理码", 10, FontStyle.Bold); b.Location = new Point(32, 220); Controls.Add(b);
        code.SetBounds(32, 252, 326, 34); Controls.Add(code);
        var note = BridgeWindow.MakeLabel("管理码只用于向自己的服务器注册设备，\n不会写入二维码或配对配置。", 9); note.ForeColor = Color.Gray; note.Location = new Point(32, 300); Controls.Add(note);
        generate.SetBounds(32, 370, 326, 46); generate.Click += async (_, _) => await Generate(); Controls.Add(generate);
        qrImage.SetBounds(392, 130, 290, 290); Controls.Add(qrImage);
        countdown.SetBounds(390, 439, 290, 58); countdown.AutoSize = false; countdown.TextAlign = ContentAlignment.MiddleCenter; Controls.Add(countdown);
        feedback.SetBounds(32, 485, 650, 68); feedback.AutoSize = false; Controls.Add(feedback);
        finish.SetBounds(445, 577, 238, 44); finish.Enabled = false; finish.Click += (_, _) => { SelectedConfig = pending!.Windows; DialogResult = DialogResult.OK; Close(); }; Controls.Add(finish);
        var cancel = BridgeWindow.MakeButton("稍后再说", false); cancel.SetBounds(32, 577, 120, 44); cancel.Click += (_, _) => Close(); Controls.Add(cancel);
        poll.Tick += async (_, _) => await Poll();
        if (File.Exists(pendingPath))
        {
            try
            {
                pending = JsonSerializer.Deserialize<PendingInvite>(ProtectedFile.Read(pendingPath), Wire.Json);
                if (pending is not null) { pending.Windows.Validate(); server.Text = pending.Windows.Server; ShowQr(); }
                else pending = null;
            }
            catch { pending = null; }
        }
    }
    private HttpClient Client(string origin, string token)
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(origin.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(18), MaxResponseContentBufferSize = 20000 };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token); return client;
    }
    private async Task Generate()
    {
        generate.Enabled = false; feedback.Text = "正在连接服务器…";
        try
        {
            if (!Uri.TryCreate(server.Text.Trim(), UriKind.Absolute, out var origin) || origin.Scheme != "https")
                throw new ArgumentException("请填写完整 HTTPS 服务器地址，可包含端口。");
            PairingDraft draft; PairingInvitation invitation;
            if (pending is not null && server.Text.Trim().TrimEnd('/') == pending.Windows.Server)
            {
                // Renew without changing the pair's content key or long-term credentials.
                var root = Convert.FromBase64String(pending.Qr[(pending.Qr.IndexOf('#') + 1)..].Replace('-', '+').Replace('_', '/') + "=");
                var droid = DecryptPending(pending, root);
                draft = new(pending.Windows, droid, new(pending.Windows.Pair, [])); invitation = draft.Invite();
                using var http = Client(pending.Windows.Server, pending.Windows.Token);
                using var result = await http.PostAsJsonAsync($"v1/pairings/{draft.Windows.Pair}/invites", invitation.Envelope, Wire.Json, lifetime.Token);
                result.EnsureSuccessStatusCode();
            }
            else
            {
                draft = PairingDraft.Create(server.Text); invitation = draft.Invite();
                if (code.Text.Trim().Length < 24) throw new ArgumentException("请填写服务器配对管理码");
                using var http = Client(draft.Windows.Server, code.Text.Trim());
                using var info = await http.GetAsync("v1/setup/info", lifetime.Token);
                if (!info.IsSuccessStatusCode) throw new InvalidOperationException("服务器尚未升级到扫码配对版本");
                using var result = await http.PostAsJsonAsync("v1/pairings", new PairingRegistration(draft.Relay, draft.Windows.Device, invitation.Envelope), Wire.Json, lifetime.Token);
                result.EnsureSuccessStatusCode();
            }
            pending = new(draft.Windows, invitation.Envelope, invitation.Qr);
            ProtectedFile.Write(pendingPath, JsonSerializer.SerializeToUtf8Bytes(pending, Wire.Json));
            code.Clear(); feedback.Text = "打开手机 ClipBridge → 扫描配对二维码。\n服务器只收到了认证摘要和加密邀请。"; ShowQr();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (HttpRequestException ex) { feedback.Text = ex.StatusCode == HttpStatusCode.Unauthorized ? "配对管理码不正确，请从服务器日志重新复制。" : "连接失败，请检查服务器地址、端口和 HTTPS 证书。"; }
        catch (ArgumentException ex) { feedback.Text = ex.Message; }
        catch (InvalidOperationException ex) { feedback.Text = ex.Message; }
        catch { feedback.Text = "暂时无法创建配对，请检查本地存储与服务器连接。"; }
        finally { if (!IsDisposed) generate.Enabled = true; }
    }
    private static ClientConfig DecryptPending(PendingInvite p, byte[] key)
    {
        byte[] data = Convert.FromBase64String(p.Envelope.Data), plain = new byte[data.Length - 16];
        try
        {
            using var aes = new System.Security.Cryptography.AesGcm(key, 16);
            aes.Decrypt(Convert.FromBase64String(p.Envelope.Nonce), data.AsSpan(0, plain.Length), data.AsSpan(plain.Length), plain, PairingDraft.InviteAad(p.Envelope.Id, p.Envelope.Expires));
            return JsonSerializer.Deserialize<ClientConfig>(plain, Wire.Json)!;
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(key); System.Security.Cryptography.CryptographicOperations.ZeroMemory(plain); }
    }
    private void ShowQr()
    {
        if (pending!.Envelope.Expires <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()) { generate.Text = "刷新二维码"; countdown.Text = "二维码已过期，请刷新"; poll.Start(); return; }
        using var generator = new QRCodeGenerator(); using var data = generator.CreateQrCode(pending!.Qr, QRCodeGenerator.ECCLevel.M);
        using var renderer = new PngByteQRCode(data); using var stream = new MemoryStream(renderer.GetGraphic(7));
        using var source = Image.FromStream(stream);
        qrImage.Image?.Dispose(); qrImage.Image = new Bitmap(source);
        generate.Text = "刷新二维码"; poll.Start();
    }
    private async Task Poll()
    {
        if (pending is null || polling) return;
        long left = pending.Envelope.Expires - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        countdown.Text = left > 0 ? $"请用手机扫码\n{left / 60:00}:{left % 60:00} 后过期" : "二维码已过期，请刷新";
        polling = true;
        try
        {
            using var http = Client(pending.Windows.Server, pending.Windows.Token);
            using var result = await http.GetAsync($"v1/pairing-invites/{pending.Envelope.Id}/status", lifetime.Token);
            if (!result.IsSuccessStatusCode) return;
            var value = await result.Content.ReadFromJsonAsync<JsonElement>(Wire.Json, lifetime.Token);
            if (value.GetProperty("status").GetString() == "claimed")
            {
                poll.Stop(); finish.Enabled = true; qrImage.Image?.Dispose(); qrImage.Image = null;
                countdown.Text = "手机已接收配对信息"; feedback.Text = "手机已扫码。点击「完成并启用此配对」开始同步。";
            }
        }
        catch (Exception) { }
        finally { polling = false; }
    }
    protected override void OnFormClosed(FormClosedEventArgs e) { lifetime.Cancel(); poll.Stop(); poll.Dispose(); qrImage.Image?.Dispose(); base.OnFormClosed(e); }
}
