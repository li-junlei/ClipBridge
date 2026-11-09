using ClipBridge;
using Microsoft.Toolkit.Uwp.Notifications;
using System.Diagnostics;
sealed partial class BridgeWindow
{
    internal static void RegisterNotificationProtocol()
    {
        using var key=Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Classes\clipbridge-desktop");key.SetValue("","URL:ClipBridge received file");key.SetValue("URL Protocol","");using var command=key.CreateSubKey(@"shell\open\command");command.SetValue("","\""+Application.ExecutablePath+"\" --notification \"%1\"");
    }
    internal static string ReceivedAction(string action,string id,string token)=>"clipbridge-desktop://"+action+"/"+id+"/"+token;
    internal static ToastContentBuilder BuildReceivedToast(string id,string name,string token)=>new ToastContentBuilder().SetProtocolActivation(new Uri(ReceivedAction("open",id,token))).AddText("文件接收完成").AddText(name)
        .AddButton(new ToastButton().SetContent("打开文件").SetProtocolActivation(new Uri(ReceivedAction("open",id,token))))
        .AddButton(new ToastButton().SetContent("打开文件所在位置").SetProtocolActivation(new Uri(ReceivedAction("folder",id,token))));
    private void ShowReceivedFile(IncomingAttachment item)
    {
        try{if(!data.NotificationTokens.TryGetValue(item.Offer.Id,out var token)){token=Guid.NewGuid().ToString("N");data.NotificationTokens[item.Offer.Id]=token;while(data.NotificationTokens.Count>256)data.NotificationTokens.Remove(data.NotificationTokens.Keys.First());Save();}BuildReceivedToast(item.Offer.Id,item.Offer.Name,token).Show(toast=>{toast.Tag=item.Offer.Id[..16];toast.ExpirationTime=DateTimeOffset.Now.AddHours(24);});}
        catch{ShowFileActions(item.Destination);}
    }
    private void HandleReceivedAction(string value)
    {
        try{if(!Uri.TryCreate(value,UriKind.Absolute,out var uri)||uri.Scheme!="clipbridge-desktop"||uri.Host is not("open" or "folder")||uri.Query.Length!=0||uri.Fragment.Length!=0)return;string[] parts=uri.AbsolutePath.Split('/',StringSplitOptions.RemoveEmptyEntries);if(parts.Length!=2)return;string id=parts[0];Wire.Id(id);Wire.Id(parts[1]);if(!data.NotificationTokens.TryGetValue(id,out var token)||!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(Convert.FromHexString(token),Convert.FromHexString(parts[1])))return;var item=data.Attachments.Incoming.FirstOrDefault(i=>i.Offer.Id==id&&i.Phase=="done"&&i.Offer.Kind=="file");if(item is null||!File.Exists(item.Destination)){Notify("文件已移动、删除或接收记录已过期。");return;}OpenReceivedFile(item.Destination,uri.Host=="folder");}catch{Notify("无法打开文件，请检查默认应用或文件位置。");}
    }
    private static void OpenReceivedFile(string path,bool folder)
    {
        if(!File.Exists(path))throw new FileNotFoundException();if(folder)Process.Start(new ProcessStartInfo("explorer.exe"){Arguments="/select,\""+Path.GetFullPath(path)+"\"",UseShellExecute=true});else Process.Start(new ProcessStartInfo(Path.GetFullPath(path)){UseShellExecute=true});
    }
    private void ShowFileActions(string path)
    {
        var window=new Form();AppStyle.Dialog(window,"文件接收完成",new Size(510,190));var label=MakeLabel(Path.GetFileName(path),12,FontStyle.Bold);label.MaximumSize=new Size(460,65);label.Location=new Point(24,24);window.Controls.Add(label);
        foreach(bool folder in new[]{false,true}){var button=MakeButton(folder?"打开文件所在位置":"打开文件",!folder);button.SetBounds(folder?242:24,112,folder?242:200,44);button.Click+=(_,_)=>{try{OpenReceivedFile(path,folder);window.Close();}catch{MessageBox.Show(window,"无法打开文件，请检查文件位置或默认应用。");}};window.Controls.Add(button);}window.Show();
    }
}
