using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClipBridge;

public record RelayDevice(string Id, string TokenHash);
public record RelayDescriptor(string Pair, List<RelayDevice> Devices);
public record InviteEnvelope(string Id, long Expires, string Nonce, string Data);
public record PairingRegistration(RelayDescriptor Relay, string Owner, InviteEnvelope Invite);
public record PairingInvitation(InviteEnvelope Envelope, string Qr);
public record PairingDraft(ClientConfig Windows, ClientConfig Android, RelayDescriptor Relay)
{
    public static PairingDraft Create(string server)
    {
        server = server.Trim().TrimEnd('/');
        string pair = Guid.NewGuid().ToString("N"), a = Guid.NewGuid().ToString("N"), b = Guid.NewGuid().ToString("N");
        string key = RandomSecret(), ta = RandomSecret(), tb = RandomSecret();
        var windows = new ClientConfig(server, pair, a, b, ta, key); windows.Validate();
        var android = new ClientConfig(server, pair, b, a, tb, key); android.Validate();
        return new(windows, android, new(pair, [new(a, TokenHash(ta)), new(b, TokenHash(tb))]));
    }
    public PairingInvitation Invite(long? now = null)
    {
        string id = Guid.NewGuid().ToString("N");
        long expires = (now ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()) + 300;
        byte[] key = RandomNumberGenerator.GetBytes(32), nonce = RandomNumberGenerator.GetBytes(12);
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(Android, Wire.Json), cipher = new byte[plain.Length + 16];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(nonce, plain, cipher.AsSpan(0, plain.Length), cipher.AsSpan(plain.Length), InviteAad(id, expires));
            var envelope = new InviteEnvelope(id, expires, Convert.ToBase64String(nonce), Convert.ToBase64String(cipher));
            string qr = $"clipbridge://pair?server={Uri.EscapeDataString(Android.Server)}&id={id}#{Base64Url(key)}";
            return new(envelope, qr);
        }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(plain); }
    }
    public static byte[] InviteAad(string id, long expires) => Encoding.ASCII.GetBytes($"clipbridge-pair:1:{id}:{expires}");
    public static string TokenHash(string token) => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    private static string RandomSecret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
