using ClipBridge;
using System.Text.Json;
using Microsoft.Win32;
sealed partial class BridgeWindow
{
    private record Details(string Pair,string Name,long LastSync);
    private void LoadDetails() { try { var d=JsonSerializer.Deserialize<Details>(ProtectedFile.Read(Path.Combine(folder,"details.bin")),Wire.Json)!; if(d.Pair==config?.Pair){peerName=d.Name;lastSync=d.LastSync;} }catch{} }
    private void SaveDetails() { if(config is not null)ProtectedFile.Write(Path.Combine(folder,"details.bin"),JsonSerializer.SerializeToUtf8Bytes(new Details(config.Pair,peerName,lastSync),Wire.Json)); }
    private void MarkSynced(){lastSync=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();SaveDetails();}
    private async Task NameLoop(RelayClient current,ClientConfig active,CancellationToken ct) {
        while(!ct.IsCancellationRequested){
            try {var name=await current.ExchangeName(active,Environment.MachineName,ct);ct.ThrowIfCancellationRequested();if(name is not null && name!=peerName){peerName=name;SaveDetails();RenderPage();}}
            catch(OperationCanceledException) when(ct.IsCancellationRequested){break;}catch{}
            try{await Task.Delay(15000,ct);}catch(OperationCanceledException){break;}
        }
    }
    private void DeletePairing(){
        if(MessageBox.Show(this,"删除本机配对配置并停止同步？对方设备的配置不会被删除。","删除配置",MessageBoxButtons.YesNo,MessageBoxIcon.Warning)!=DialogResult.Yes)return;
        connectionStop?.Cancel();relay?.Dispose();
        try {
            // Keep only encrypted replay counters so reimporting an old file cannot replay old content.
            if(config is not null){state.Pending=null;var dir=Path.Combine(folder,"profiles",config.Pair);Directory.CreateDirectory(dir);ProtectedFile.Write(Path.Combine(dir,"state.bin"),JsonSerializer.SerializeToUtf8Bytes(state,Wire.Json));}
            if(Directory.Exists(Path.Combine(folder,"profiles")))foreach(var f in Directory.EnumerateFiles(Path.Combine(folder,"profiles"),"config.bin",SearchOption.AllDirectories))File.Delete(f);
            foreach(var name in new[]{"config.bin","state.bin","details.bin","pending-pairing.bin"})File.Delete(Path.Combine(folder,name));
            config=null;state=new();peerName="";lastSync=0;lastObserved=null;paused=false;status.Text="未配对";RenderPage();
        }catch{paused=true;MessageBox.Show(this,"删除未完成，已停止同步，请检查文件权限。","ClipBridge");}
    }
    private static bool AutoStart(){using var key=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");return key?.GetValue("ClipBridge") is string;}
    private void SetAutoStart(bool enabled){try{using var key=Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");if(enabled)key.SetValue("ClipBridge","\""+Application.ExecutablePath+"\" --startup");else key.DeleteValue("ClipBridge",false);}catch{MessageBox.Show(this,"无法修改开机自启动设置。","ClipBridge");RenderPage();}}
    private string PeerTitle => config is null ? "未配对" : string.IsNullOrWhiteSpace(peerName)?"等待设备名称":peerName;
    private string SyncTime => lastSync==0?"暂无同步记录":DateTimeOffset.FromUnixTimeMilliseconds(lastSync).LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");
}
