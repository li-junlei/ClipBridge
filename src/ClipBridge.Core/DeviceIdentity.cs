using System.Security.Cryptography;
using System.Text;
namespace ClipBridge;
public record PeerDevice(string Name,string Installation);
public static class DeviceIdentity
{
    public static ClientConfig Channel(ClientConfig c) => c with { Key = Convert.ToBase64String(HMACSHA256.HashData(Convert.FromBase64String(c.Key), Encoding.ASCII.GetBytes("clipbridge:device-name:1"))) };
    public static Envelope Seal(ClientConfig c, string name) { Validate(name); return Wire.Seal(Channel(c), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), name); }
    public static string Open(ClientConfig c, Envelope e) { return Read(c,e).Name; }
    public static Envelope Seal(ClientConfig c,string name,string installation){Validate(name);Wire.Id(installation);return Wire.Seal(Channel(c),DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),System.Text.Json.JsonSerializer.Serialize(new PeerDevice(name,installation),Wire.Json));}
    public static PeerDevice Read(ClientConfig c,Envelope e){var text=Wire.Open(Channel(c),e).Text;if(!text.StartsWith("{")){Validate(text);return new(text,"");}var peer=System.Text.Json.JsonSerializer.Deserialize<PeerDevice>(text,Wire.Json)??throw new ArgumentException();Validate(peer.Name);Wire.Id(peer.Installation);return peer;}
    public static void Validate(string name) { if (string.IsNullOrWhiteSpace(name) || name.Length > 80 || name.Any(char.IsControl)) throw new ArgumentException("Invalid device name"); }
}
