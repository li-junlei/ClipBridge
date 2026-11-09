using System.Text.Json;
namespace ClipBridge;
public static class WebDavPairing
{
    public static PairingDraft Create(string server,string username,string password){
        var draft=PairingDraft.Create("https://example.invalid");
        ClientConfig Configure(ClientConfig c)=>c with{Server=server.Trim().TrimEnd('/')+"/",Transport="webdav",Username=username,Password=password};
        var a=Configure(draft.Windows);var b=Configure(draft.Android);a.Validate();b.Validate();return new(a,b,draft.Relay);
    }
    public static string Qr(ClientConfig android){android.Validate();if(android.Transport!="webdav")throw new ArgumentException();
        var qr="clipbridge://webdav#"+Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(android,Wire.Json)).TrimEnd('=').Replace('+','-').Replace('/','_');
        if(qr.Length>2200)throw new ArgumentException("配置过长，无法生成二维码，请缩短地址或使用较短的应用密码。");return qr;}
}
