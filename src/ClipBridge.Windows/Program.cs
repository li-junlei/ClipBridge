using ClipBridge;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.IO.Pipes;
using Microsoft.Win32;
internal static class Program
{
    [STAThread] private static void Main(string[] args){ApplicationConfiguration.Initialize();string sid=System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;using var instance=new Mutex(true,@"Local\ClipBridge-"+sid,out bool first);
        if(!first){try{using var pipe=new NamedPipeClientStream(".","ClipBridge-"+sid,PipeDirection.Out);pipe.Connect(2000);using var writer=new StreamWriter(pipe);writer.WriteLine(JsonSerializer.Serialize(args));}catch{MessageBox.Show("ClipBridge 已在运行，请从托盘打开。");}return;}
        using var guard=new Mutex(false,@"Local\ClipBridge.Install");Application.Run(new BridgeWindow(args,sid));}
}
sealed class ClipboardBatch{public string Id{get;set;}=Guid.NewGuid().ToString("N");public string Text{get;set;}="";public long Clock{get;set;}public List<string> Targets{get;set;}=[];public List<string> Done{get;set;}=[];public List<string> Groups{get;set;}=[];public List<string> DoneGroups{get;set;}=[];public long Expires{get;set;}=DateTimeOffset.UtcNow.ToUnixTimeSeconds()+600;}
sealed class DesktopState{public string Identity{get;set;}=Guid.NewGuid().ToString("N");public List<BridgeProfile> Profiles{get;set;}=[];public Dictionary<string,SyncState> Tombstones{get;set;}=[];public List<string> Seen{get;set;}=[];public Dictionary<string,long> SeenClocks{get;set;}=[];public long NextClock{get;set;}public bool Paused{get;set;}public AttachmentState Attachments{get;set;}=new();public ClipboardBatch? Clipboard{get;set;}public string ReceiveDirectory{get;set;}="";}
sealed partial class BridgeWindow:Form
{
    private readonly string folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ClipBridge");
    private DesktopState data=new();private readonly CancellationTokenSource stop=new();private readonly Dictionary<string,(RelayClient Client,CancellationTokenSource Stop)> running=[];
    private readonly NotifyIcon tray;private readonly Icon appIcon=new(typeof(BridgeWindow).Assembly.GetManifestResourceStream("ClipBridge.App.ico")!),trayIcon=new(typeof(BridgeWindow).Assembly.GetManifestResourceStream("ClipBridge.Tray.ico")!);
    private readonly ToolStripMenuItem status=new("ClipBridge");private readonly List<string> pendingShellFiles=[];private bool shellDispatching;private readonly string[] launchArgs;private readonly string sid;private bool paused{get=>data.Paused;set=>data.Paused=value;}private bool exiting,started;private string? lastObserved,lastImage;private uint suppressedSequence;private TransferHub hub=null!;
    private readonly System.Windows.Forms.Timer uiTimer=new(){Interval=1000};private readonly Panel page=new(){Dock=DockStyle.Fill,AutoScroll=true,Padding=new Padding(24)};private int selectedPage;
    public BridgeWindow(string[] args,string sid){launchArgs=args;this.sid=sid;Text="ClipBridge";Icon=appIcon;Directory.CreateDirectory(folder);
        try{if(File.Exists(Path.Combine(folder,"desktop-v2.bin")))data=JsonSerializer.Deserialize<DesktopState>(ProtectedFile.Read(Path.Combine(folder,"desktop-v2.bin")),Wire.Json)!;else Migrate();Wire.Id(data.Identity);foreach(var p in data.Profiles){p.Config.Validate();p.State.Validate(p.Config);}}catch{var old=Path.Combine(folder,"desktop-v2.bin");if(File.Exists(old))File.Copy(old,old+".backup-"+DateTimeOffset.UtcNow.ToUnixTimeSeconds(),true);data=new();MessageBox.Show("配置读取失败，已停止自动同步。请保留本地数据并检查后重新导入配置。");}
        if(data.ReceiveDirectory.Length==0)data.ReceiveDirectory=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"Downloads","ClipBridge");
        var menu=new ContextMenuStrip();menu.Items.Add(status);menu.Items.Add("打开 ClipBridge",null,(_,_)=>OpenDashboard());menu.Items.Add("发送文件…",null,async(_,_)=>await ChooseFiles());menu.Items.Add("导入配置…",null,(_,_)=>Import());var pause=new ToolStripMenuItem("暂停全部同步"){CheckOnClick=true,Checked=paused};pause.CheckedChanged+=(_,_)=>{paused=pause.Checked;if(paused){data.Clipboard=null;foreach(var p in data.Profiles)p.State.Pending=null;}Save();RenderPage();};menu.Items.Add(pause);menu.Items.Add("取消待发送附件",null,(_,_)=>hub.CancelAll());menu.Items.Add("退出",null,(_,_)=>{exiting=true;Close();});
        tray=new(){Icon=trayIcon,Text="ClipBridge",ContextMenuStrip=menu,Visible=true};tray.DoubleClick+=(_,_)=>OpenDashboard();
        hub=new(()=>paused?[]:data.Profiles.ToList(),data.Attachments,Path.Combine(folder,"attachments"),data.Identity,Save);
        hub.Notice=Notify;hub.Requested=IncomingRequest;hub.Received=IncomingReady;BuildDashboard();Save();if(File.Exists(Path.Combine(folder,"config.bin"))){File.Delete(Path.Combine(folder,"config.bin"));File.Delete(Path.Combine(folder,"state.bin"));File.Delete(Path.Combine(folder,"details.bin"));}}
    private void Migrate(){
        string cp=Path.Combine(folder,"config.bin");if(!File.Exists(cp))return;var c=JsonSerializer.Deserialize<ClientConfig>(ProtectedFile.Read(cp),Wire.Json)!;var state=JsonSerializer.Deserialize<SyncState>(ProtectedFile.Read(Path.Combine(folder,"state.bin")),Wire.Json)!;state.Validate(c);var current=new BridgeProfile{Config=c,State=state};
        try{string path=Path.Combine(folder,"details.bin");if(File.Exists(path)){using var json=JsonDocument.Parse(ProtectedFile.Read(path));if(json.RootElement.GetProperty("pair").GetString()==c.Pair){current.Name=json.RootElement.GetProperty("name").GetString()??"";current.LastSync=json.RootElement.GetProperty("lastSync").GetInt64();}}}catch{}
        data.Profiles.Add(current);
        if(state.Pending is{} pending)try{var payload=Wire.Open(c with{Device=c.Peer,Peer=c.Device},pending);data.Clipboard=new(){Text=payload.Text,Expires=payload.Expires,Targets=[c.Pair],Clock=++data.NextClock};}catch{}
        string archive=Path.Combine(folder,"profiles");if(Directory.Exists(archive))foreach(string path in Directory.GetFiles(archive,"config.bin",SearchOption.AllDirectories)){try{var old=JsonSerializer.Deserialize<ClientConfig>(ProtectedFile.Read(path),Wire.Json)!;old.Validate();if(data.Profiles.Any(p=>p.Config.Pair==old.Pair)||data.Profiles.Count>=16)continue;var saved=JsonSerializer.Deserialize<SyncState>(ProtectedFile.Read(Path.Combine(Path.GetDirectoryName(path)!,"state.bin")),Wire.Json)!;saved.Validate(old);data.Profiles.Add(new(){Config=old,State=saved,Enabled=false});}catch{}}
    }
    private void Save(){try{ProtectedFile.Write(Path.Combine(folder,"desktop-v2.bin"),JsonSerializer.SerializeToUtf8Bytes(data,Wire.Json));}catch{paused=true;throw;}}
    protected override void OnHandleCreated(EventArgs e){base.OnHandleCreated(e);if(!AddClipboardFormatListener(Handle))throw new Win32Exception();}
    protected override void OnShown(EventArgs e){base.OnShown(e);started=true;Reconcile();_=SendLoop();_=AttachmentLoop();_=PipeLoop();_=HandleArguments(launchArgs);if(launchArgs.Contains("--startup"))Hide();}
    private async Task PipeLoop(){while(!stop.IsCancellationRequested){try{using var server=new NamedPipeServerStream("ClipBridge-"+sid,PipeDirection.In,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);await server.WaitForConnectionAsync(stop.Token);using var reader=new StreamReader(server);string? line=await reader.ReadLineAsync(stop.Token);if(line is not null&&line.Length<65536){var args=JsonSerializer.Deserialize<string[]>(line);if(args is not null)await HandleArguments(args);}}catch(OperationCanceledException){break;}catch{}}}
    private async Task HandleArguments(string[] args){int i=Array.IndexOf(args,"--send");if(i>=0){foreach(string path in args.Skip(i+1).Take(20))if(!pendingShellFiles.Contains(path))pendingShellFiles.Add(path);if(!shellDispatching){shellDispatching=true;await Task.Delay(500);while(pendingShellFiles.Count>0){var paths=pendingShellFiles.Take(20).ToArray();pendingShellFiles.RemoveRange(0,paths.Length);await SendFiles(paths,true);}shellDispatching=false;}}else if(!args.Contains("--startup"))OpenDashboard();}
    private void Reconcile(){foreach(var pair in running.Keys.ToArray())if(!data.Profiles.Any(p=>p.Enabled&&p.Config.Pair==pair)){running[pair].Stop.Cancel();running[pair].Client.Dispose();running.Remove(pair);}foreach(var p in data.Profiles.Where(p=>p.Enabled)){if(running.ContainsKey(p.Config.Pair))continue;var cancellation=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);var client=new RelayClient(p.Config);running[p.Config.Pair]=(client,cancellation);_=ReceiveLoop(p,client,cancellation.Token);_=NameLoop(p,client,cancellation.Token);}}
    private async Task NameLoop(BridgeProfile p,RelayClient client,CancellationToken ct){while(!ct.IsCancellationRequested){try{var peer=await client.ExchangeDevice(p.Config,Environment.MachineName,data.Identity,ct);ct.ThrowIfCancellationRequested();if(peer is not null){p.Name=peer.Name;p.PeerIdentity=peer.Installation;Save();}}catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}catch{}try{await Task.Delay(15000,ct);}catch(OperationCanceledException){break;}}}
    private async Task SendLoop(){while(!stop.IsCancellationRequested){try{var batch=data.Clipboard;if(!paused && batch is not null && batch.Expires>DateTimeOffset.UtcNow.ToUnixTimeSeconds()){
                    foreach(var known in data.Profiles.Where(p=>batch.Targets.Contains(p.Config.Pair))){if(!batch.Groups.Contains(known.Group))batch.Groups.Add(known.Group);if(batch.Done.Contains(known.Config.Pair)&&!batch.DoneGroups.Contains(known.Group))batch.DoneGroups.Add(known.Group);}
                    foreach(var group in data.Profiles.Where(p=>p.Enabled&&(batch.Targets.Contains(p.Config.Pair)||batch.Groups.Contains(p.Group))).GroupBy(p=>p.Group).ToList()){
                        if(batch.DoneGroups.Contains(group.Key)||group.Any(p=>batch.Done.Contains(p.Config.Pair)))continue;
                        foreach(var p in group){if(!running.TryGetValue(p.Config.Pair,out var route))continue;try{
                                long clock=checked(p.State.Clock+1);var envelope=Wire.Seal(p.Config,clock,batch.Text,now:batch.Expires-600,eventId:batch.Id,origin:data.Identity,eventClock:batch.Clock);p.State.Clock=clock;p.State.Winner=p.Config.Device;p.State.Pending=envelope;Save();await route.Client.Send(envelope,route.Stop.Token);route.Stop.Token.ThrowIfCancellationRequested();
                                p.State.Pending=null;p.LastSync=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();p.Status="文本已发送";foreach(var done in group)if(!batch.Done.Contains(done.Config.Pair))batch.Done.Add(done.Config.Pair);if(!batch.DoneGroups.Contains(group.Key))batch.DoneGroups.Add(group.Key);Save();break;
                            }catch(OperationCanceledException)when(stop.IsCancellationRequested){throw;}catch{p.Status="通道失败，尝试其他配置";p.State.Pending=null;Save();}}
                    }
                }}catch(OperationCanceledException){break;}catch{status.Text="同步暂不可用";}try{await Task.Delay(1000,stop.Token);}catch(OperationCanceledException){break;}}}
    private async Task ReceiveLoop(BridgeProfile p,RelayClient client,CancellationToken ct){string after="";while(!ct.IsCancellationRequested){try{if(paused){await Task.Delay(1000,ct);continue;}var envelope=await client.Receive(after,ct);ct.ThrowIfCancellationRequested();if(!p.Enabled)continue;if(envelope is null){p.Status="连接正常";continue;}after=envelope.Id;var payload=p.State.Accept(p.Config,envelope);Save();if(payload is null)continue;
                string eventKey=payload.Origin+":"+payload.EventId;if(payload.EventId.Length!=0){Wire.Id(payload.EventId);Wire.Id(payload.Origin);if(p.PeerIdentity.Length==32 && p.PeerIdentity!=payload.Origin)throw new CryptographicException();if(p.PeerIdentity.Length==0)p.PeerIdentity=payload.Origin;if(data.Seen.Contains(eventKey) || payload.EventClock>0 && data.SeenClocks.GetValueOrDefault(payload.Origin)>=payload.EventClock)continue;if(payload.EventClock>0)data.SeenClocks[payload.Origin]=payload.EventClock;data.Seen.Add(eventKey);while(data.Seen.Count>512)data.Seen.RemoveAt(0);Save();}
                if(payload.Text!=lastObserved){for(int attempt=0;attempt<10;attempt++){try{ct.ThrowIfCancellationRequested();if(paused||!p.Enabled)break;lastObserved=payload.Text;if(payload.Text.Length==0)Clipboard.Clear();else Clipboard.SetText(payload.Text);suppressedSequence=GetClipboardSequenceNumber();break;}catch(ExternalException){await Task.Delay(100,ct);}}}
                p.LastSync=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();p.Status="文本已接收";Save();
            }catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}catch{p.Status="连接或消息验证失败";try{await Task.Delay(3000,ct);}catch(OperationCanceledException){break;}}}}
    protected override void WndProc(ref Message m){if(m.Msg==0x031D && started&&!paused&&GetClipboardSequenceNumber()!=suppressedSequence){try{
                if(Clipboard.ContainsImage()){using var source=Clipboard.GetImage();if(source is not null && (long)source.Width*source.Height<=20000000){var bitmap=new Bitmap(source);_=SendCopiedImage(bitmap);}}
                else if(Clipboard.ContainsText()){string text=Clipboard.GetText();if(text!=lastObserved){lastObserved=text;data.Clipboard=new(){Text=text,Clock=checked(++data.NextClock),Targets=data.Profiles.Where(p=>p.Enabled).Select(p=>p.Config.Pair).ToList(),Groups=data.Profiles.Where(p=>p.Enabled).Select(p=>p.Group).Distinct().ToList()};Save();}}
            }catch{status.Text="剪贴板暂不可用";}}base.WndProc(ref m);}
    private async Task SendCopiedImage(Bitmap bitmap){string path=Path.Combine(folder,"attachments","clipboard-"+Guid.NewGuid().ToString("N")+".png");try{await Task.Run(()=>{using(bitmap)bitmap.Save(path,System.Drawing.Imaging.ImageFormat.Png);});using var stream=File.OpenRead(path);string hash=Convert.ToHexString(await SHA256.HashDataAsync(stream));if(hash==lastImage)return;lastImage=hash;foreach(var group in data.Profiles.Where(p=>p.Enabled).GroupBy(p=>p.Group).ToList())await hub.Stage(path,"image",group.Select(p=>p.Config.Pair).ToList(),stop.Token);}catch{Notify("图片发送失败；图片上限 100 MB，请检查配置和图片大小。");}finally{if(File.Exists(path))File.Delete(path);}}
    private async Task AttachmentLoop(){while(!stop.IsCancellationRequested){try{if(!paused)await hub.Tick(stop.Token);}catch(OperationCanceledException)when(stop.IsCancellationRequested){break;}catch{status.Text="附件连接暂不可用";}try{await Task.Delay(hub.Busy?100:1000,stop.Token);}catch(OperationCanceledException){break;}}}
    private void Notify(string message){tray.ShowBalloonTip(5000,"ClipBridge",message,ToolTipIcon.Info);status.Text=message;}
    private void IncomingRequest(IncomingAttachment incoming){var dialog=new Form{Text="接收文件",ClientSize=new Size(420,190),StartPosition=FormStartPosition.CenterScreen,TopMost=true};var label=MakeLabel(incoming.Offer.Name+"\n"+(incoming.Offer.Size/1048576d).ToString("0.##")+" MB",12);label.Location=new Point(20,20);label.MaximumSize=new Size(380,95);dialog.Controls.Add(label);bool answered=false;
        foreach(bool accept in new[]{true,false}){var button=MakeButton(accept?"同意接收":"拒绝",accept);button.SetBounds(accept?160:285,130,110,40);button.Click+=async(_,_)=>{answered=true;await hub.Decide(incoming.Offer.Id,accept);dialog.Close();};dialog.Controls.Add(button);}dialog.FormClosed+=async(_,_)=>{if(!answered)await hub.Decide(incoming.Offer.Id,false);};dialog.Show();Notify("收到文件请求："+incoming.Offer.Name);}
    private async Task IncomingReady(IncomingAttachment incoming){
        try{
            if(incoming.Offer.Kind=="image"){
                using(var image=Image.FromFile(incoming.Path)){if((long)image.Width*image.Height>20000000)throw new InvalidDataException();using var bitmap=new Bitmap(image);Clipboard.SetImage(bitmap);suppressedSequence=GetClipboardSequenceNumber();}Notify("图片已写入剪贴板");
            }else{
                Directory.CreateDirectory(data.ReceiveDirectory);string name=Attachments.SafeName(incoming.Offer.Name),path=incoming.Destination;
                if(path.Length==0){path=Path.Combine(data.ReceiveDirectory,name);if(File.Exists(path)){using var existing=File.OpenRead(path);string hash=Convert.ToHexString(await SHA256.HashDataAsync(existing)).ToLowerInvariant();if(hash!=incoming.Offer.Hash)path=Path.Combine(data.ReceiveDirectory,Path.GetFileNameWithoutExtension(name)+"-"+incoming.Offer.Id+Path.GetExtension(name));}incoming.Destination=path;Save();}
                if(File.Exists(path)){using var existing=File.OpenRead(path);if(Convert.ToHexString(await SHA256.HashDataAsync(existing)).ToLowerInvariant()!=incoming.Offer.Hash)throw new IOException("Destination already exists");}
                else{string temp=path+".clipbridge-part";try{using(var source=File.OpenRead(incoming.Path))using(var output=new FileStream(temp,FileMode.Create,FileAccess.Write,FileShare.None)){await source.CopyToAsync(output);output.Flush(true);}File.Move(temp,path,false);}finally{if(File.Exists(temp))File.Delete(temp);}try{await File.WriteAllTextAsync(path+":Zone.Identifier","[ZoneTransfer]\r\nZoneId=3\r\n");}catch(IOException){}}
                Notify("文件已保存："+name);
            }
            await hub.Complete(incoming,true);
        }catch{Notify("接收后保存失败，请检查接收目录或剪贴板。");await hub.Complete(incoming,false);}
    }
    private async Task ChooseFiles(){using var dialog=new OpenFileDialog{Multiselect=true,Title="通过 ClipBridge 发送文件"};if(dialog.ShowDialog(this)==DialogResult.OK)await SendFiles(dialog.FileNames);}
    private async Task SendFiles(string[] paths,bool shell=false){if(paused){Notify("全部同步已暂停，请先恢复。");return;}if(paths.Length==0)return;if(paths.Length>20){Notify("一次最多选择 20 个文件。");return;}var groups=data.Profiles.Where(p=>p.Enabled).GroupBy(p=>p.Group).ToList();if(groups.Count==0){Notify("请先开启至少一套配置。");return;}using var chooser=new Form{Text="选择接收设备",ClientSize=new Size(420,300),StartPosition=FormStartPosition.CenterScreen};var list=new ListBox{Dock=DockStyle.Top,Height=220};foreach(var group in groups)list.Items.Add(group.First().Title+" · "+group.Count()+" 条通道");list.SelectedIndex=0;chooser.Controls.Add(list);var send=MakeButton("发送请求",true);send.SetBounds(270,240,125,40);send.Click+=(_,_)=>chooser.DialogResult=DialogResult.OK;chooser.Controls.Add(send);if(chooser.ShowDialog(this)!=DialogResult.OK)return;var targets=groups[list.SelectedIndex].Select(p=>p.Config.Pair).ToList();if(shell&&pendingShellFiles.Count>0){var added=pendingShellFiles.Take(Math.Max(0,20-paths.Length)).ToArray();pendingShellFiles.RemoveRange(0,added.Length);paths=paths.Concat(added).Distinct().ToArray();}foreach(string path in paths){try{if(!File.Exists(path))throw new IOException();await hub.Stage(path,"file",targets,stop.Token);}catch(ArgumentException e){MessageBox.Show(this,e.Message,"ClipBridge");}catch{Notify("无法读取所选文件。");}}}
    private void Import(){try{using var dialog=new OpenFileDialog{Filter="ClipBridge 配置 (*.json)|*.json"};if(dialog.ShowDialog(this)!=DialogResult.OK)return;if(new FileInfo(dialog.FileName).Length>8192)throw new InvalidDataException();UsePairing(JsonSerializer.Deserialize<ClientConfig>(File.ReadAllText(dialog.FileName),Wire.Json)!);}catch{MessageBox.Show(this,"无法导入配置，请检查文件。","ClipBridge");}}
    private void UsePairing(ClientConfig c){c.Validate();var existing=data.Profiles.FirstOrDefault(p=>p.Config.Pair==c.Pair);if(existing is not null){if(existing.Config!=c)throw new ArgumentException("同一配对不能覆盖密钥或地址，请新建配对。");existing.Enabled=true;}else{if(data.Profiles.Count>=16)throw new ArgumentException("最多保存 16 套配置。");var state=data.Tombstones.GetValueOrDefault(c.Pair)??new();state.Validate(c);state.Pending=null;data.Profiles.Add(new(){Config=c,State=state});}Save();if(started)Reconcile();RenderPage();}
    protected override void OnFormClosing(FormClosingEventArgs e){if(!exiting&&e.CloseReason==CloseReason.UserClosing){e.Cancel=true;Hide();return;}base.OnFormClosing(e);}
    protected override void OnFormClosed(FormClosedEventArgs e){stop.Cancel();foreach(var run in running.Values){run.Stop.Cancel();run.Client.Dispose();}uiTimer.Dispose();tray.Dispose();appIcon.Dispose();trayIcon.Dispose();RemoveClipboardFormatListener(Handle);base.OnFormClosed(e);}
    [DllImport("user32.dll",SetLastError=true)]private static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")]private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")]private static extern uint GetClipboardSequenceNumber();
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
