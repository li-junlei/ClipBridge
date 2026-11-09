using System.Security.Cryptography;
using System.Text;
namespace ClipBridge;
public static class DeviceIdentity
{
    public static ClientConfig Channel(ClientConfig c) => c with { Key = Convert.ToBase64String(HMACSHA256.HashData(Convert.FromBase64String(c.Key), Encoding.ASCII.GetBytes("clipbridge:device-name:1"))) };
    public static Envelope Seal(ClientConfig c, string name) { Validate(name); return Wire.Seal(Channel(c), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), name); }
    public static string Open(ClientConfig c, Envelope e) { var name = Wire.Open(Channel(c), e).Text; Validate(name); return name; }
    public static void Validate(string name) { if (string.IsNullOrWhiteSpace(name) || name.Length > 80 || name.Any(char.IsControl)) throw new ArgumentException("Invalid device name"); }
}
