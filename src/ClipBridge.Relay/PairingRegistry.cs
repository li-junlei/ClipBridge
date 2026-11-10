using ClipBridge;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

sealed record RegisteredPair(RelayDescriptor Relay, string Owner, InviteEnvelope? Invite = null, string? ClaimedTicket = null, long ClaimedExpiry = 0);
sealed record DeviceIdentity(string Pair, string Id);

sealed class PairingRegistry
{
    private readonly object gate = new();
    private readonly string path;
    private readonly Dictionary<string, RegisteredPair> pairs;
    private readonly byte[] setupHash;
    public PairingRegistry(RelayDescriptor? legacy, string directory)
    {
        if (legacy is not null) ValidateRelay(legacy);
        Directory.CreateDirectory(directory); path = Path.Combine(directory, "pairings.state");
        pairs = File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, RegisteredPair>>(File.ReadAllText(path), Wire.Json)
            ?? throw new InvalidDataException("Invalid pairing registry.") : new();
        foreach (var pair in pairs.Values) ValidateRelay(pair.Relay);
        if (legacy is not null && !pairs.ContainsKey(legacy.Pair)) { pairs.Add(legacy.Pair, new(legacy, legacy.Devices[0].Id)); Save(); }
        string secretPath = Path.Combine(directory, "setup-code");
        string? supplied = Environment.GetEnvironmentVariable("CLIPBRIDGE_SETUP_CODE");
        string code;
        if (!string.IsNullOrEmpty(supplied)) code = supplied;
        else if (File.Exists(secretPath)) code = File.ReadAllText(secretPath).Trim();
        else { code = "CB-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant(); WritePrivate(secretPath, Encoding.UTF8.GetBytes(code)); }
        if (code.Length < 24 || code.Length > 96) throw new InvalidDataException("Invalid setup code.");
        setupHash = SHA256.HashData(Encoding.UTF8.GetBytes(code));
        // Deliberate owner-only bootstrap output. Never include this code in HTTP responses or QR invitations.
        Console.WriteLine("ClipBridge 配对管理码（仅用于创建同步组或旧版配对，请勿分享）: " + code);
    }
    public bool IsSetup(string? token) => token is not null && CryptographicOperations.FixedTimeEquals(
        setupHash, SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    public DeviceIdentity? Authorize(string token)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        lock (gate)
            foreach (var pair in pairs.Values)
                foreach (var d in pair.Relay.Devices)
                    if (CryptographicOperations.FixedTimeEquals(hash, Convert.FromBase64String(d.TokenHash))) return new(pair.Relay.Pair, d.Id);
        return null;
    }
    public bool HasDevice(string pair, string id)
    { lock (gate) return pairs.TryGetValue(pair, out var p) && p.Relay.Devices.Any(d => d.Id == id); }
    public void Register(PairingRegistration request)
    {
        ValidateRelay(request.Relay); Wire.Id(request.Owner); ValidateInvite(request.Invite);
        if (!request.Relay.Devices.Any(d => d.Id == request.Owner)) throw new ArgumentException("Owner is not a device.");
        lock (gate)
        {
            if (pairs.ContainsKey(request.Relay.Pair)) throw new InvalidOperationException("Pair already exists.");
            if (pairs.Count >= 64) throw new InvalidOperationException("Pair limit reached.");
            var ids = pairs.Values.SelectMany(p => p.Relay.Devices).Select(d => d.Id).ToHashSet();
            if (request.Relay.Devices.Any(d => ids.Contains(d.Id))) throw new InvalidOperationException("Device ID already exists.");
            if (pairs.Values.Any(p => p.Invite?.Id == request.Invite.Id || p.ClaimedTicket == request.Invite.Id)) throw new InvalidOperationException("Invite already exists.");
            pairs.Add(request.Relay.Pair, new(request.Relay, request.Owner, request.Invite));
            try { Save(); } catch { pairs.Remove(request.Relay.Pair); throw; }
        }
    }
    public void Renew(string pair, string owner, InviteEnvelope invite)
    {
        ValidateInvite(invite);
        lock (gate)
        {
            if (!pairs.TryGetValue(pair, out var previous) || previous.Owner != owner) throw new UnauthorizedAccessException();
            if (pairs.Values.Any(p => p.Invite?.Id == invite.Id || p.ClaimedTicket == invite.Id)) throw new InvalidOperationException("Invite already exists.");
            pairs[pair] = previous with { Invite = invite, ClaimedTicket = null, ClaimedExpiry = 0 };
            try { Save(); } catch { pairs[pair] = previous; throw; }
        }
    }
    public InviteEnvelope? Claim(string ticket)
    {
        Wire.Id(ticket);
        lock (gate)
        {
            var p = pairs.Values.FirstOrDefault(p => p.Invite?.Id == ticket);
            if (p?.Invite is not { } invite || invite.Expires <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return null;
            pairs[p.Relay.Pair] = p with { Invite = null, ClaimedTicket = ticket, ClaimedExpiry = invite.Expires };
            try { Save(); } catch { pairs[p.Relay.Pair] = p; throw; }
            return invite;
        }
    }
    public string Status(string pair, string owner, string ticket)
    {
        Wire.Id(ticket);
        lock (gate)
        {
            if (!pairs.TryGetValue(pair, out var p) || p.Owner != owner) throw new UnauthorizedAccessException();
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (p.ClaimedTicket == ticket) return "claimed";
            return p.Invite?.Id == ticket && p.Invite.Expires > now ? "waiting" : "expired";
        }
    }
    private static void ValidateRelay(RelayDescriptor c)
    {
        if (c is null || c.Devices is null || c.Devices.Any(d => d is null)) throw new ArgumentException("Invalid devices.");
        Wire.Id(c.Pair);
        if (c.Devices.Count != 2 || c.Devices.Select(d => d.Id).Distinct().Count() != 2) throw new ArgumentException("Two distinct devices required.");
        foreach (var d in c.Devices) { Wire.Id(d.Id); if (Convert.FromBase64String(d.TokenHash).Length != 32) throw new ArgumentException("Invalid token digest."); }
        if (c.Devices[0].TokenHash == c.Devices[1].TokenHash) throw new ArgumentException("Device tokens must differ.");
    }
    private static void ValidateInvite(InviteEnvelope e)
    {
        Wire.Id(e.Id); long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (e.Expires <= now || e.Expires > now + 360 || Convert.FromBase64String(e.Nonce).Length != 12 ||
            Convert.FromBase64String(e.Data).Length is < 16 or > 8192) throw new ArgumentException("Invalid or expired invite.");
    }
    private void Save() => WritePrivate(path, JsonSerializer.SerializeToUtf8Bytes(pairs, Wire.Json));
    private static void WritePrivate(string file, byte[] bytes)
    {
        using (var stream = new FileStream(file + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
        { if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file + ".tmp", UnixFileMode.UserRead | UnixFileMode.UserWrite); stream.Write(bytes); stream.Flush(true); }
        File.Move(file + ".tmp", file, true);
    }
}
