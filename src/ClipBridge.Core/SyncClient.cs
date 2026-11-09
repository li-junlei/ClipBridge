using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace ClipBridge;

// Call from a single synchronization loop. State and pending ciphertext must be saved together.
public sealed class SyncState
{
    public long Clock { get; set; }
    public string Winner { get; set; } = "";
    public Envelope? Pending { get; set; }
    public void Validate(ClientConfig c)
    {
        if (Clock < 0 || Clock > 9007199254740991L || Winner is null ||
            (Clock == 0 ? Winner.Length != 0 : Winner != c.Device && Winner != c.Peer))
            throw new ArgumentException("Invalid replay state; re-pair required.");
        if (Pending is { } e)
        {
            Wire.Validate(e);
            if (e.Pair != c.Pair || e.From != c.Device || e.To != c.Peer || e.Clock != Clock || Winner != c.Device)
                throw new ArgumentException("Pending message does not match replay state.");
        }
    }
    public Envelope Queue(ClientConfig c, string text)
    {
        long next = checked(Clock + 1);
        var e = Wire.Seal(c, next, text);
        Clock = next; Winner = c.Device; Pending = e;
        return e;
    }
    public ClipPayload? Accept(ClientConfig c, Envelope e)
    {
        var p = Wire.Open(c, e);
        if (e.Clock < Clock || (e.Clock == Clock && string.CompareOrdinal(e.From, Winner) <= 0)) return null;
        Clock = e.Clock; Winner = e.From; Pending = null;
        return p;
    }
}
public sealed class RelayClient : IDisposable
{
    private readonly HttpClient http;
    private readonly WebDavTransport? webdav;
    public RelayClient(ClientConfig c)
    {
        c.Validate();
        http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            { BaseAddress = new Uri(c.Server.TrimEnd('/')+"/"), Timeout = TimeSpan.FromSeconds(35), MaxResponseContentBufferSize = 600000 };
        http.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache=true,NoStore=true };
        http.DefaultRequestHeaders.Authorization = c.Transport=="webdav" ? new AuthenticationHeaderValue("Basic",Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(c.Username+":"+c.Password))) : new AuthenticationHeaderValue("Bearer", c.Token);
        if(c.Transport=="webdav")webdav=new WebDavTransport(http,c);
    }
    public async Task Send(Envelope e, CancellationToken ct)
    {
        if(webdav is not null){await webdav.Put(false,e,ct);return;}
        using var response = await http.PostAsJsonAsync("v1/messages", e, Wire.Json, ct);
        response.EnsureSuccessStatusCode();
    }
    public async Task<Envelope?> Receive(string after, CancellationToken ct)
    {
        if(webdav is not null){var e=await webdav.Get(false,ct);if(e is null || e.Id==after){await Task.Delay(1000,ct);return null;}return e;}
        using var response = await http.GetAsync("v1/inbox?after=" + Uri.EscapeDataString(after), ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<Envelope?>(Wire.Json, ct);
    }
    public async Task<string?> ExchangeName(ClientConfig config, string name, CancellationToken ct)
    {
        if(webdav is not null){await webdav.Put(true,DeviceIdentity.Seal(config,name),ct);var peer=await webdav.Get(true,ct);return peer is null?null:DeviceIdentity.Open(config,peer);}
        using var sent = await http.PostAsJsonAsync("v1/device-info", DeviceIdentity.Seal(config, name), Wire.Json, ct); sent.EnsureSuccessStatusCode();
        using var received = await http.GetAsync("v1/device-info", ct); received.EnsureSuccessStatusCode();
        var envelope = await received.Content.ReadFromJsonAsync<Envelope?>(Wire.Json, ct);
        return envelope is null ? null : DeviceIdentity.Open(config, envelope);
    }
    public async Task<PeerDevice?> ExchangeDevice(ClientConfig config,string name,string installation,CancellationToken ct){
        var envelope=DeviceIdentity.Seal(config,name,installation);
        if(webdav is not null){await webdav.Put(true,envelope,ct);var peer=await webdav.Get(true,ct);return peer is null?null:DeviceIdentity.Read(config,peer);}
        using var sent=await http.PostAsJsonAsync("v1/device-info",envelope,Wire.Json,ct);sent.EnsureSuccessStatusCode();using var received=await http.GetAsync("v1/device-info",ct);received.EnsureSuccessStatusCode();var e=await received.Content.ReadFromJsonAsync<Envelope?>(Wire.Json,ct);return e is null?null:DeviceIdentity.Read(config,e);
    }
    public void Dispose() => http.Dispose();
}
