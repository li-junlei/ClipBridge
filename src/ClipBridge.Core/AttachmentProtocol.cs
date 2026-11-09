using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace ClipBridge;

public record AttachmentOffer(string Id,string Name,string Mime,string Kind,long Size,string Hash,int Chunks,long Expires,string Status,string Sender);
public record AttachmentDecision(string Id,string Status);
public record AttachmentIndex(List<AttachmentOffer> Offers,List<AttachmentDecision> Decisions){public long Created{get;init;}public long Expires{get;init;}}
public static class Attachments
{
    public const int ChunkSize=256*1024;
    public const long MaxSize=100L*1024*1024;
    public static void Validate(AttachmentOffer o){
        Wire.Id(o.Id);Wire.Id(o.Sender);
        if(o.Kind is not ("file" or "image") || o.Status is not ("offered" or "ready" or "cancelled") || o.Size<0 || o.Size>MaxSize || o.Chunks!=Math.Max(1,(o.Size+ChunkSize-1)/ChunkSize) || !System.Text.RegularExpressions.Regex.IsMatch(o.Hash,"\\A[0-9a-f]{64}\\z") || string.IsNullOrWhiteSpace(o.Name) || o.Name.Length>180 || o.Name.Any(char.IsControl) || o.Name.IndexOfAny(['/', '\\', ':'])>=0 || o.Name is "." or ".." || o.Mime.Length>100)throw new ArgumentException("Invalid attachment");
    }
    public static string SafeName(string name){var s=new string(name.Select(c=>char.IsControl(c)||"<>:\"/\\|?*".Contains(c)?'_':c).ToArray()).Trim(' ','.');if(s.Length==0)s="file";if(s.Length>180)s=s[..180];if(new[]{"CON","PRN","AUX","NUL","COM1","COM2","COM3","COM4","LPT1","LPT2","LPT3"}.Contains(s.Split('.')[0],StringComparer.OrdinalIgnoreCase))s="_"+s;return s;}
    public static ClientConfig IndexConfig(ClientConfig c)=>c with{Key=Convert.ToBase64String(HMACSHA256.HashData(Convert.FromBase64String(c.Key),Encoding.ASCII.GetBytes("clipbridge:attachments-index:1")))};
    public static Envelope SealIndex(ClientConfig c,AttachmentIndex index,long? now=null){
        c.Validate();long time=now??DateTimeOffset.UtcNow.ToUnixTimeSeconds();var e=new Envelope(1,c.Pair,c.Device,c.Peer,Guid.NewGuid().ToString("N"),time*1000,Convert.ToBase64String(RandomNumberGenerator.GetBytes(12)),"");
        byte[] plain=JsonSerializer.SerializeToUtf8Bytes(index with{Created=time,Expires=time+3600},Wire.Json);if(plain.Length>200000)throw new ArgumentException("Manifest too large");byte[] header=Wire.Header(e),key=HMACSHA256.HashData(Convert.FromBase64String(IndexConfig(c).Key),header),data=new byte[plain.Length+16];
        try{using var aes=new AesGcm(key,16);aes.Encrypt(Convert.FromBase64String(e.Nonce),plain,data.AsSpan(0,plain.Length),data.AsSpan(plain.Length,16),header);return e with{Data=Convert.ToBase64String(data)};}finally{CryptographicOperations.ZeroMemory(key);CryptographicOperations.ZeroMemory(plain);}
    }
    public static AttachmentIndex OpenIndex(ClientConfig c,Envelope envelope){
        c.Validate();Wire.Validate(envelope);if(envelope.Pair!=c.Pair||envelope.From!=c.Peer||envelope.To!=c.Device)throw new CryptographicException();
        byte[] header=Wire.Header(envelope),key=HMACSHA256.HashData(Convert.FromBase64String(IndexConfig(c).Key),header),data=Convert.FromBase64String(envelope.Data),plain=new byte[data.Length-16];
        try{using var aes=new AesGcm(key,16);aes.Decrypt(Convert.FromBase64String(envelope.Nonce),data.AsSpan(0,plain.Length),data.AsSpan(plain.Length,16),plain,header);var result=JsonSerializer.Deserialize<AttachmentIndex>(plain,Wire.Json)??throw new CryptographicException();long now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();if(result.Created>now+60||result.Expires<=now||result.Expires<result.Created||result.Expires-result.Created>3600||result.Offers.Count>32||result.Decisions.Count>128)throw new CryptographicException();foreach(var o in result.Offers)Validate(o);foreach(var d in result.Decisions){Wire.Id(d.Id);if(d.Status is not("accepted" or "rejected" or "done"))throw new CryptographicException();}return result;}finally{CryptographicOperations.ZeroMemory(key);CryptographicOperations.ZeroMemory(plain);}
    }
    private static byte[] Aad(ClientConfig c,string from,string to,string id,int part){Wire.Id(id);Wire.Id(from);Wire.Id(to);if(part<0 || part>=400)throw new ArgumentException();return Encoding.ASCII.GetBytes($"clipbridge:attachment:1:{c.Pair}:{from}:{to}:{id}:{part}");}
    public static byte[] Encrypt(ClientConfig c,string id,int part,ReadOnlySpan<byte> plain){if(plain.Length>ChunkSize)throw new ArgumentException();var aad=Aad(c,c.Device,c.Peer,id,part);var key=HMACSHA256.HashData(Convert.FromBase64String(c.Key),aad);var output=new byte[plain.Length+28];RandomNumberGenerator.Fill(output.AsSpan(0,12));try{using var aes=new AesGcm(key,16);aes.Encrypt(output.AsSpan(0,12),plain,output.AsSpan(12,plain.Length),output.AsSpan(12+plain.Length,16),aad);return output;}finally{CryptographicOperations.ZeroMemory(key);}}
    public static byte[] Decrypt(ClientConfig c,string id,int part,byte[] cipher){if(cipher.Length<28 || cipher.Length>ChunkSize+28)throw new CryptographicException();var aad=Aad(c,c.Peer,c.Device,id,part);var key=HMACSHA256.HashData(Convert.FromBase64String(c.Key),aad);var plain=new byte[cipher.Length-28];try{using var aes=new AesGcm(key,16);aes.Decrypt(cipher.AsSpan(0,12),cipher.AsSpan(12,plain.Length),cipher.AsSpan(12+plain.Length,16),plain,aad);return plain;}finally{CryptographicOperations.ZeroMemory(key);}}
}
