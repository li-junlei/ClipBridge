using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClipBridge;

public record ClientConfig(string Server, string Pair, string Device, string Peer, string Token, string Key)
{
    public string Transport { get; init; } = "relay";
    public string Username { get; init; } = "";
    public string Password { get; init; } = "";
    public void Validate()
    {
        if(Transport is not ("relay" or "webdav")) throw new ArgumentException("Unknown transport.");
        var uri = new Uri(Server);
        if (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback))
            throw new ArgumentException("Only HTTPS or loopback HTTP is permitted.");
        if (uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || (Transport == "relay" && uri.AbsolutePath != "/"))
            throw new ArgumentException("Server must be an origin URL.");
        if(Transport=="webdav" && (string.IsNullOrWhiteSpace(Username) || Username.Length>256 || Username.Contains(':') || Username.Any(char.IsControl) || Password.Length is <1 or >1024 || Password.Any(char.IsControl))) throw new ArgumentException("Invalid WebDAV credentials.");
        foreach (var id in new[] { Pair, Device, Peer }) Wire.Id(id);
        if (Device == Peer || Convert.FromBase64String(Key).Length != 32 || Convert.FromBase64String(Token).Length != 32)
            throw new ArgumentException("Invalid pairing configuration.");
    }
}
public record Envelope(int Version, string Pair, string From, string To, string Id, long Clock, string Nonce, string Data);
public record ClipPayload(string Kind, string Text, long Created, long Expires);
public static class Wire
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public const int MaxText = 65536;
    public static void Id(string value)
    {
        if (!Regex.IsMatch(value, "\\A[0-9a-f]{32}\\z")) throw new ArgumentException("Invalid ID.");
    }
    public static byte[] Header(Envelope e)
    {
        foreach (var id in new[] { e.Pair, e.From, e.To, e.Id }) Id(id);
        if (e.Version != 1 || e.Clock < 1 || e.Clock > 9007199254740991L || e.From == e.To)
            throw new ArgumentException("Invalid envelope.");
        return Encoding.ASCII.GetBytes($"clipbridge:1:{e.Pair}:{e.From}:{e.To}:{e.Id}:{e.Clock}");
    }
    public static void Validate(Envelope e)
    {
        Header(e);
        if (Convert.FromBase64String(e.Nonce).Length != 12) throw new ArgumentException("Invalid nonce.");
        var size = Convert.FromBase64String(e.Data).Length;
        if (size < 16 || size > 400000) throw new ArgumentException("Invalid ciphertext size.");
    }
    // Each random message ID derives a distinct subkey. Header also authenticates routing and ordering.
    public static Envelope Seal(ClientConfig c, long clock, string text, long? now = null)
    {
        c.Validate();
        if (Encoding.UTF8.GetByteCount(text) > MaxText) throw new ArgumentException("Text exceeds 64 KiB.");
        long time = now ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var e = new Envelope(1, c.Pair, c.Device, c.Peer, Guid.NewGuid().ToString("N"), clock,
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(12)), "");
        var header = Header(e);
        var key = HMACSHA256.HashData(Convert.FromBase64String(c.Key), header);
        var plain = JsonSerializer.SerializeToUtf8Bytes(new ClipPayload("text", text, time, time + 600), Json);
        var data = new byte[plain.Length + 16];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(Convert.FromBase64String(e.Nonce), plain, data.AsSpan(0, plain.Length), data.AsSpan(plain.Length), header);
            return e with { Data = Convert.ToBase64String(data) };
        }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(plain); }
    }
    public static ClipPayload Open(ClientConfig c, Envelope e, long? now = null)
    {
        c.Validate(); Validate(e);
        if (e.Pair != c.Pair || e.From != c.Peer || e.To != c.Device) throw new CryptographicException("Wrong peer.");
        var header = Header(e);
        var key = HMACSHA256.HashData(Convert.FromBase64String(c.Key), header);
        var data = Convert.FromBase64String(e.Data);
        var plain = new byte[data.Length - 16];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(Convert.FromBase64String(e.Nonce), data.AsSpan(0, plain.Length), data.AsSpan(plain.Length), plain, header);
            var p = JsonSerializer.Deserialize<ClipPayload>(plain, Json) ?? throw new CryptographicException("Invalid payload.");
            long time = now ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (p.Kind != "text" || p.Text is null || Encoding.UTF8.GetByteCount(p.Text) > MaxText ||
                p.Created > time + 60 || p.Expires <= time || p.Expires < p.Created || p.Expires - p.Created > 600)
                throw new CryptographicException("Expired or invalid payload.");
            return p;
        }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(plain); }
    }
}
