using ClipBridge;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 600000);
builder.Logging.ClearProviders(); // No request bodies, tokens, or clipboard values in logs.
var configPath = Environment.GetEnvironmentVariable("CLIPBRIDGE_RELAY_CONFIG") ?? "private/relay.json";
var config = File.Exists(configPath) ? JsonSerializer.Deserialize<RelayDescriptor>(File.ReadAllText(configPath), Wire.Json)
    ?? throw new InvalidOperationException("Invalid legacy relay configuration.") : null;
string dataDirectory = Environment.GetEnvironmentVariable("CLIPBRIDGE_DATA") ?? "data";
var registry = new PairingRegistry(config, dataDirectory);
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.AddPolicy("group-pairing",context=>RateLimitPartition.GetFixedWindowLimiter("group:"+context.Connection.RemoteIpAddress,_=>new FixedWindowRateLimiterOptions{PermitLimit=300,Window=TimeSpan.FromMinutes(1),QueueLimit=0}));
    o.AddPolicy("attachments",context=>RateLimitPartition.GetFixedWindowLimiter((string?)context.Items["device"]??"anonymous",_=>new FixedWindowRateLimiterOptions{PermitLimit=6000,Window=TimeSpan.FromMinutes(1),QueueLimit=0}));
    o.AddPolicy("device", context => RateLimitPartition.GetFixedWindowLimiter(
        (string?)context.Items["device"] ?? "public:" + context.Connection.RemoteIpAddress, _ => new FixedWindowRateLimiterOptions
        { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
var store = new InboxStore(dataDirectory);
var app = builder.Build();
app.Use(async (context, next) =>
{
    if(context.Request.Path.StartsWithSegments("/v2")){await next(context);return;}
    if (context.Request.Path == "/health" || context.Request.Path == "/v1/setup/info" ||
        (context.Request.Path == "/v1/pairing-invites/claim" && context.Request.Method == "POST"))
    { await next(context); return; }
    string auth = context.Request.Headers.Authorization.ToString();
    if (!auth.StartsWith("Bearer ", StringComparison.Ordinal) || auth.Length > 128)
    { context.Response.StatusCode = 401; return; }
    if (context.Request.Path == "/v1/pairings" && context.Request.Method == "POST")
    {
        if (!registry.IsSetup(auth[7..])) { context.Response.StatusCode = 401; return; }
        context.Items["device"] = "setup"; await next(context); return;
    }
    var device = registry.Authorize(auth[7..]);
    if (device is null) { context.Response.StatusCode = 401; return; }
    context.Items["device"] = device.Id;
    context.Items["pair"] = device.Pair;
    await next(context);
});
app.UseRateLimiter();
app.MapGroupRoutes(registry.IsSetup,dataDirectory);
app.MapGet("/health", () => Results.Ok(new { status = "ok", protocol = 1 }));
app.MapGet("/v1/setup/info", () => Results.Ok(new { pairing = 1, deviceInfo = 1, attachments = 1, groups = 1, requiresSetupCode = true }));
app.MapPost("/v1/pairings", async (HttpContext ctx) =>
{
    try
    {
        var request = await ctx.Request.ReadFromJsonAsync<PairingRegistration>(Wire.Json, ctx.RequestAborted);
        if (request?.Relay is null || request.Invite is null) return Results.BadRequest();
        registry.Register(request); return Results.Ok(new { status = "registered" });
    }
    catch (InvalidOperationException) { return Results.Conflict(); }
    catch (Exception ex) when (ex is ArgumentException or FormatException or JsonException or BadHttpRequestException) { return Results.BadRequest(); }
}).RequireRateLimiting("device");
app.MapPost("/v1/pairings/{pair}/invites", async (HttpContext ctx, string pair) =>
{
    if (pair != (string)ctx.Items["pair"]!) return Results.StatusCode(403);
    try
    {
        var invite = await ctx.Request.ReadFromJsonAsync<InviteEnvelope>(Wire.Json, ctx.RequestAborted);
        if (invite is null) return Results.BadRequest();
        registry.Renew(pair, (string)ctx.Items["device"]!, invite); return Results.Ok();
    }
    catch (UnauthorizedAccessException) { return Results.StatusCode(403); }
    catch (InvalidOperationException) { return Results.Conflict(); }
    catch (Exception ex) when (ex is ArgumentException or FormatException or JsonException) { return Results.BadRequest(); }
}).RequireRateLimiting("device");
app.MapPost("/v1/pairing-invites/claim", async (HttpContext ctx) =>
{
    ctx.Response.Headers.CacheControl = "no-store";
    try
    {
        var request = await ctx.Request.ReadFromJsonAsync<ClaimRequest>(Wire.Json, ctx.RequestAborted);
        if (request is null) return Results.BadRequest();
        var invite = registry.Claim(request.Ticket);
        return invite is null ? (IResult)Results.StatusCode(410) : Results.Json(invite, Wire.Json);
    }
    catch (Exception ex) when (ex is ArgumentException or JsonException) { return Results.BadRequest(); }
}).RequireRateLimiting("device");
app.MapGet("/v1/pairing-invites/{ticket}/status", (HttpContext ctx, string ticket) =>
{
    ctx.Response.Headers.CacheControl = "no-store";
    try { return Results.Ok(new { status = registry.Status((string)ctx.Items["pair"]!, (string)ctx.Items["device"]!, ticket) }); }
    catch (UnauthorizedAccessException) { return Results.StatusCode(403); }
    catch (ArgumentException) { return Results.BadRequest(); }
}).RequireRateLimiting("device");
app.MapPost("/v1/messages", async (HttpContext ctx) =>
{
    try
    {
        var e = await ctx.Request.ReadFromJsonAsync<Envelope>(Wire.Json, ctx.RequestAborted);
        if (e is null) return Results.BadRequest();
        Wire.Validate(e);
        if (e.Pair != (string)ctx.Items["pair"]! || e.From != (string)ctx.Items["device"]! || !registry.HasDevice(e.Pair, e.To))
            return Results.StatusCode(403);
        return store.Put(e) ? Results.Ok() : Results.Conflict();
    }
    catch (Exception ex) when (ex is JsonException or ArgumentException or FormatException or BadHttpRequestException)
    { return Results.BadRequest(); }
}).RequireRateLimiting("device");
app.MapGet("/v1/inbox", async (HttpContext ctx, string? after) =>
{
    if (after is not null && after.Length != 0)
    { try { Wire.Id(after); } catch (ArgumentException) { return Results.BadRequest(); } }
    var e = await store.Get((string)ctx.Items["device"]!, after ?? "", ctx.RequestAborted);
    // ASP.NET's Json(null) emits an empty body; clients require a JSON null on idle polls.
    if (e is null) return Results.Text("null", "application/json");
    return Results.Json(e, Wire.Json);
}).RequireRateLimiting("device");
// Device names remain encrypted and independent from the clipboard inbox.
var names = new System.Collections.Concurrent.ConcurrentDictionary<string, Envelope>();
app.MapPost("/v1/device-info", async (HttpContext ctx) =>
{
    try {
        var e = await ctx.Request.ReadFromJsonAsync<Envelope>(Wire.Json, ctx.RequestAborted);
        if (e is null || e.Data.Length > 2048) return Results.BadRequest();
        Wire.Validate(e);
        if(e.Pair != (string)ctx.Items["pair"]! || e.From != (string)ctx.Items["device"]! || !registry.HasDevice(e.Pair,e.To)) return Results.StatusCode(403);
        names.AddOrUpdate(e.To,e,(_,old)=>e.Clock>old.Clock?e:old); return Results.Ok();
    } catch(Exception ex) when(ex is ArgumentException or FormatException or JsonException or BadHttpRequestException) { return Results.BadRequest(); }
}).RequireRateLimiting("device");
app.MapGet("/v1/device-info", (HttpContext ctx) => {
    ctx.Response.Headers.CacheControl="no-store";
    return names.TryGetValue((string)ctx.Items["device"]!,out var e) ? Results.Json(e,Wire.Json) : Results.Text("null","application/json");
}).RequireRateLimiting("device");
// Opaque attachment index and bounded encrypted chunks. No names or keys are decoded here.
var objectGate=new SemaphoreSlim(1,1);var objectUsage=new Dictionary<string,(long Bytes,int Count,DateTime Scanned)>();
string ObjectPath(HttpContext ctx,string device,string key)=>Path.Combine(dataDirectory,"attachments",(string)ctx.Items["pair"]!,device,key+".blob");
bool ObjectKey(string key)=>key=="index" || System.Text.RegularExpressions.Regex.IsMatch(key,"\\A[0-9a-f]{32}-(?:[0-9]|[1-9][0-9]|[1-3][0-9]{2})\\z");
app.MapPut("/v1/objects/{device}/{key}",async(HttpContext ctx,string device,string key)=>{
    if(device!=(string)ctx.Items["device"]! || !ObjectKey(key))return Results.StatusCode(403);
    using var memory=new MemoryStream();var buffer=new byte[8192];int n;
    while((n=await ctx.Request.Body.ReadAsync(buffer,ctx.RequestAborted))>0){if(memory.Length+n>(key=="index"?600000:Attachments.ChunkSize+28))return Results.StatusCode(413);memory.Write(buffer,0,n);}
    var data=memory.ToArray();if(key=="index"){
        try{var e=JsonSerializer.Deserialize<Envelope>(data,Wire.Json);if(e is null)return Results.BadRequest();Wire.Validate(e);if(e.Pair!=(string)ctx.Items["pair"]! || e.From!=device || !registry.HasDevice(e.Pair,e.To))return Results.StatusCode(403);}catch(Exception ex) when(ex is ArgumentException or FormatException or JsonException){return Results.BadRequest();}
    }else if(data.Length<28)return Results.BadRequest();
    await objectGate.WaitAsync(ctx.RequestAborted);try{
        string path=ObjectPath(ctx,device,key),directory=Path.GetDirectoryName(path)!;Directory.CreateDirectory(directory);
        if(!objectUsage.TryGetValue(directory,out var usage)||usage.Scanned<DateTime.UtcNow.AddMinutes(-1)){
            foreach(var old in Directory.GetFiles(directory,"*.blob")){if(File.GetLastWriteTimeUtc(old)<DateTime.UtcNow.AddDays(-1))File.Delete(old);}
            var currentFiles=Directory.GetFiles(directory,"*.blob");usage=(currentFiles.Sum(f=>new FileInfo(f).Length),currentFiles.Length,DateTime.UtcNow);
        }
        long previous=File.Exists(path)?new FileInfo(path).Length:0;if(usage.Bytes-previous+data.Length>512L*1024*1024 || previous==0 && usage.Count>=8192)return Results.StatusCode(507);
        string temp=path+".tmp";await File.WriteAllBytesAsync(temp,data,ctx.RequestAborted);File.Move(temp,path,true);objectUsage[directory]=(usage.Bytes-previous+data.Length,usage.Count+(previous==0?1:0),usage.Scanned);return Results.Ok();
    }finally{objectGate.Release();}
}).RequireRateLimiting("attachments");
app.MapGet("/v1/objects/{device}/{key}",(HttpContext ctx,string device,string key)=>{
    if(!ObjectKey(key) || !registry.HasDevice((string)ctx.Items["pair"]!,device))return Results.StatusCode(403);
    string path=ObjectPath(ctx,device,key);ctx.Response.Headers.CacheControl="no-store";
    if(!File.Exists(path) || File.GetLastWriteTimeUtc(path)<DateTime.UtcNow.AddDays(-1))return Results.NotFound();
    return Results.File(File.Open(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete),"application/octet-stream");
}).RequireRateLimiting("attachments");

app.MapDelete("/v1/objects/{device}/{key}",async(HttpContext ctx,string device,string key)=>{
    if(device!=(string)ctx.Items["device"]!||!ObjectKey(key))return Results.StatusCode(403);
    await objectGate.WaitAsync(ctx.RequestAborted);try{string path=ObjectPath(ctx,device,key),directory=Path.GetDirectoryName(path)!;if(File.Exists(path)){long length=new FileInfo(path).Length;File.Delete(path);if(objectUsage.TryGetValue(directory,out var usage))objectUsage[directory]=(Math.Max(0,usage.Bytes-length),Math.Max(0,usage.Count-1),usage.Scanned);}return Results.Ok();}finally{objectGate.Release();}
}).RequireRateLimiting("attachments");
app.Run();

record ClaimRequest(string Ticket);
sealed class InboxStore
{
    private readonly object gate = new();
    private readonly string directory;
    private readonly Dictionary<string, Envelope> inbox = new();
    private TaskCompletionSource changed = NewSignal();
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public InboxStore(string directory)
    {
        this.directory = directory; Directory.CreateDirectory(directory);
        foreach (var path in Directory.GetFiles(directory, "*.json"))
        {
            // Registry metadata lives beside inboxes; only recipient-ID files are clipboard envelopes.
            if (Path.GetFileNameWithoutExtension(path).Length != 32) continue;
            var e = JsonSerializer.Deserialize<Envelope>(File.ReadAllText(path), Wire.Json)
                ?? throw new InvalidDataException("Corrupt inbox.");
            Wire.Validate(e); inbox[e.To] = e;
        }
    }
    public bool Put(Envelope e)
    {
        lock (gate)
        {
            if (inbox.TryGetValue(e.To, out var previous))
            {
                if (e == previous) return true;
                if (e.Clock <= previous.Clock) return false;
            }
            string path = Path.Combine(directory, e.To + ".json");
            var bytes = JsonSerializer.SerializeToUtf8Bytes(e, Wire.Json);
            using (var file = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
            { file.Write(bytes); file.Flush(true); }
            File.Move(path + ".tmp", path, true);
            inbox[e.To] = e;
            var old = changed; changed = NewSignal(); old.TrySetResult();
            return true;
        }
    }
    public async Task<Envelope?> Get(string device, string after, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (true)
        {
            Task signal;
            lock (gate)
            {
                if (inbox.TryGetValue(device, out var e) && e.Id != after) return e;
                signal = changed.Task;
            }
            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero) return null;
            try { await signal.WaitAsync(left, ct); } catch (TimeoutException) { return null; }
        }
    }
}
