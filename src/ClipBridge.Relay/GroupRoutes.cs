using ClipBridge;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.Features;

sealed record GroupRelayRegistration(string Id,string AuthHash);
sealed record GroupInviteRegistration(GroupInviteMeta Meta,string OwnerHash);
sealed record GroupInviteRecord(GroupInviteMeta Meta,string OwnerHash,string? GuestHash=null);
static class GroupRoutes
{
    private static readonly object Gate=new();
    private static readonly Dictionary<string,long> Writers=[];
    private static readonly Regex Locator=new("\\A[23456789ABCDEFGHJKLMNPQRSTUVWXYZ]{6}\\z");
    private static readonly HashSet<string> HostSlots=["a2.json","a3.json","ahello.json","aapproval.json","grant.json"];
    private static readonly HashSet<string> GuestSlots=["b1.json","b2.json","b3.json","bhello.json","bapproval.json","done.json"];
    private static bool Token(string supplied,string expected){try{return supplied.Length<=128&&CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(supplied)),Convert.FromBase64String(expected));}catch(FormatException){return false;}}
    private static string Bearer(HttpContext ctx){string auth=ctx.Request.Headers.Authorization.ToString();return auth.StartsWith("Bearer ",StringComparison.Ordinal)?auth[7..]:"";}
    private static string Basic(HttpContext ctx){try{string auth=ctx.Request.Headers.Authorization.ToString();if(!auth.StartsWith("Basic ",StringComparison.Ordinal)||auth.Length>2048)return "";string value=Encoding.UTF8.GetString(Convert.FromBase64String(auth[6..]));return value.StartsWith("group:",StringComparison.Ordinal)?value[6..]:"";}catch(FormatException){return "";}}
    private static void Write<T>(string path,T value){Directory.CreateDirectory(Path.GetDirectoryName(path)!);string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";try{File.WriteAllBytes(temp,JsonSerializer.SerializeToUtf8Bytes(value,Wire.Json));File.Move(temp,path,true);}finally{if(File.Exists(temp))File.Delete(temp);}}
    private static T? Read<T>(string path)where T:class=>File.Exists(path)?JsonSerializer.Deserialize<T>(File.ReadAllBytes(path),Wire.Json):null;
    public static void MapGroupRoutes(this WebApplication app,Func<string?,bool> setup,string directory)
    {
        string root=Path.Combine(directory,"groups-v2"),invites=Path.Combine(directory,"group-invites-v2");Directory.CreateDirectory(root);Directory.CreateDirectory(invites);
        bool Authorized(string id,string token){try{Wire.Id(id);var record=Read<GroupRelayRegistration>(Path.Combine(root,id,"registration.json"));return record is not null&&Token(token,record.AuthHash);}catch(ArgumentException){return false;}}
        app.MapPost("/v2/groups",async(HttpContext ctx)=>{
            if(!setup(Bearer(ctx)))return Results.Unauthorized();try{var value=await ctx.Request.ReadFromJsonAsync<GroupRelayRegistration>(Wire.Json,ctx.RequestAborted)??throw new ArgumentException();Wire.Id(value.Id);if(Convert.FromBase64String(value.AuthHash).Length!=32)throw new ArgumentException();lock(Gate){string file=Path.Combine(root,value.Id,"registration.json");if(File.Exists(file))return Results.Conflict();if(Directory.EnumerateDirectories(root).Count()>=64)return Results.StatusCode(429);Write(file,value);}return Results.Ok(new{status="created"});}catch(Exception e)when(e is ArgumentException or JsonException or FormatException or BadHttpRequestException){return Results.BadRequest();}
        }).RequireRateLimiting("group-pairing");
        app.MapPost("/v2/groups/{group}/invitations/{id}",async(HttpContext ctx,string group,string id)=>{
            if(!Authorized(group,Bearer(ctx)))return Results.Unauthorized();if(!Locator.IsMatch(id))return Results.BadRequest();try{
                var request=await ctx.Request.ReadFromJsonAsync<GroupInviteRegistration>(Wire.Json,ctx.RequestAborted)??throw new ArgumentException();var meta=request.Meta;long now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();if(meta.Group!=group||meta.Expires<=now||meta.Expires>now+360||!Regex.IsMatch(meta.Root,"\\A[0-9a-f]{64}\\z")||meta.First.Round!=1||meta.First.Values.Length!=6||meta.First.Values.Any(s=>s.Length>800)||Convert.FromBase64String(request.OwnerHash).Length!=32)throw new ArgumentException();
                lock(Gate){foreach(string folder in Directory.EnumerateDirectories(invites)){var old=Read<GroupInviteRecord>(Path.Combine(folder,"record.json"));if(old is not null&&old.Meta.Expires<now-60)Directory.Delete(folder,true);}string path=Path.Combine(invites,id,"record.json");if(File.Exists(path))return Results.Conflict();if(Directory.EnumerateDirectories(invites).Count()>=128)return Results.StatusCode(429);Write(path,new GroupInviteRecord(meta,request.OwnerHash));}return Results.Ok(new{status="waiting"});
            }catch(Exception e)when(e is ArgumentException or JsonException or FormatException or BadHttpRequestException){return Results.BadRequest();}
        }).RequireRateLimiting("group-pairing");
        app.MapMethods("/v2/invitations/{id}/{slot}",["GET","PUT"],async(HttpContext ctx,string id,string slot)=>{
            if(!Locator.IsMatch(id)||slot!="meta.json"&&!HostSlots.Contains(slot)&&!GuestSlots.Contains(slot))return Results.BadRequest();string folder=Path.Combine(invites,id),recordPath=Path.Combine(folder,"record.json");
            var record=Read<GroupInviteRecord>(recordPath);if(record is null)return Results.NotFound();if(record.Meta.Expires<=DateTimeOffset.UtcNow.ToUnixTimeSeconds())return Results.StatusCode(410);
            if(ctx.Request.Method=="GET"){ctx.Response.Headers.CacheControl="no-store";return slot=="meta.json"?Results.Json(record.Meta,Wire.Json):File.Exists(Path.Combine(folder,slot))?Results.File(Path.Combine(folder,slot),"application/json"):Results.NotFound();}
            if(slot=="meta.json")return Results.StatusCode(403);string token=Bearer(ctx);if(token.Length is <32 or >96)return Results.Unauthorized();
            if(HostSlots.Contains(slot)&&!Token(token,record.OwnerHash)||GuestSlots.Contains(slot)&&record.GuestHash is not null&&!Token(token,record.GuestHash))return Results.Unauthorized();
            if(GuestSlots.Contains(slot)&&record.GuestHash is null&&slot!="b1.json")return Results.StatusCode(409);
            try{byte[] bytes;using(var body=new MemoryStream()){await ctx.Request.Body.CopyToAsync(body,ctx.RequestAborted);if(body.Length>200000)return Results.StatusCode(413);bytes=body.ToArray();}using var json=JsonDocument.Parse(bytes);if(json.RootElement.ValueKind!=JsonValueKind.Object)return Results.BadRequest();
                lock(Gate){record=Read<GroupInviteRecord>(recordPath);if(record is null||record.Meta.Expires<=DateTimeOffset.UtcNow.ToUnixTimeSeconds())return Results.StatusCode(410);if(GuestSlots.Contains(slot)){if(record.GuestHash is not null&&!Token(token,record.GuestHash))return Results.Unauthorized();if(record.GuestHash is null){record=record with{GuestHash=Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(token)))};Write(recordPath,record);}}string target=Path.Combine(folder,slot);if(File.Exists(target))return Results.StatusCode(412);File.WriteAllBytes(target,bytes);}return Results.Ok();
            }catch(JsonException){return Results.BadRequest();}
        }).RequireRateLimiting("group-pairing");
        app.MapMethods("/v2/groups/{group}/store/{**key}",["GET","PUT","DELETE","MKCOL"],async(HttpContext ctx,string group,string? key)=>{
            if(!Authorized(group,Basic(ctx)))return Results.Unauthorized();if(key is null||key.Length>500)return Results.BadRequest();string[] parts=key.Split('/',StringSplitOptions.RemoveEmptyEntries);if(parts.Length is <1 or >8||parts.Any(s=>s is "." or ".."||!Regex.IsMatch(s,"\\A[a-z0-9][a-z0-9._-]{0,120}\\z")))return Results.BadRequest();
            string store=Path.GetFullPath(Path.Combine(root,group,"objects")),path=Path.GetFullPath(Path.Combine([store,..parts]));if(!path.StartsWith(store+Path.DirectorySeparatorChar,StringComparison.Ordinal))return Results.BadRequest();
            if(ctx.Request.Method=="MKCOL")return Results.StatusCode(405);
            if(ctx.Request.Method=="GET"){ctx.Response.Headers.CacheControl="no-cache, no-store";return File.Exists(path)?Results.File(path,"application/octet-stream"):Results.NotFound();}
            if(ctx.Request.Method=="DELETE"){lock(Gate){if(Writers.ContainsKey(path))return Results.StatusCode(409);if(File.Exists(path))File.Delete(path);}return Results.NoContent();}
            const long limit=100L*1024*1024+11200;bool bundle=path.EndsWith(".cbx",StringComparison.Ordinal);long max=bundle?limit:600000;if(ctx.Request.ContentLength is not long size||size<0||size>max)return Results.StatusCode(413);
            ctx.Features.Get<IHttpMaxRequestBodySizeFeature>()!.MaxRequestBodySize=max;
            lock(Gate){if(Writers.ContainsKey(path))return Results.StatusCode(409);Directory.CreateDirectory(store);long used=0;int count=0;foreach(string file in Directory.EnumerateFiles(store,"*",SearchOption.AllDirectories)){used+=new FileInfo(file).Length;count++;}if(count>=8192||used+size+Writers.Where(w=>w.Key.StartsWith(store+Path.DirectorySeparatorChar,StringComparison.Ordinal)).Sum(w=>w.Value)>2L*1024*1024*1024)return Results.StatusCode(507);if(ctx.Request.Headers.IfNoneMatch=="*"&&File.Exists(path))return Results.StatusCode(412);Writers.Add(path,size);}
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try{await using(var output=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None,65536,true)){await ctx.Request.Body.CopyToAsync(output,ctx.RequestAborted);if(output.Length!=size)return Results.BadRequest();await output.FlushAsync(ctx.RequestAborted);}File.Move(temp,path,true);return Results.Ok();}finally{lock(Gate){Writers.Remove(path);}if(File.Exists(temp))File.Delete(temp);}
        });
    }
}
