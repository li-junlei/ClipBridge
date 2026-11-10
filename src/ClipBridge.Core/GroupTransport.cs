using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace ClipBridge;

public sealed record GroupInviteMeta(string Group,string Root,long Expires,PakeMessage First);
public sealed record GroupJoinLink(GroupConnection Connection,string Code);
public sealed class GroupHttp:IDisposable
{
    private readonly HttpClient http;
    private readonly bool dav;
    private readonly HashSet<string> directories=[];
    public GroupHttp(string root,string? username=null,string? password=null,string? bearer=null){http=new(new HttpClientHandler{AllowAutoRedirect=false}){BaseAddress=new Uri(root.TrimEnd('/')+"/"),Timeout=TimeSpan.FromSeconds(30),MaxResponseContentBufferSize=250000};dav=username is not null;if(dav)http.DefaultRequestHeaders.Authorization=new("Basic",Convert.ToBase64String(Encoding.UTF8.GetBytes(username+":"+password)));else if(bearer is not null)http.DefaultRequestHeaders.Authorization=new("Bearer",bearer);http.DefaultRequestHeaders.CacheControl=new(){NoCache=true,NoStore=true};}
    public async Task Ensure(string path,CancellationToken ct){if(!dav)return;string parent="";foreach(string segment in path.Split('/',StringSplitOptions.RemoveEmptyEntries)){parent+=segment+"/";if(directories.Contains(parent))continue;using var request=new HttpRequestMessage(new HttpMethod("MKCOL"),parent);using var response=await http.SendAsync(request,ct);if(response.StatusCode!=HttpStatusCode.MethodNotAllowed&&!response.IsSuccessStatusCode){using var probe=new HttpRequestMessage(new HttpMethod("PROPFIND"),parent);probe.Headers.TryAddWithoutValidation("Depth","0");using var existing=await http.SendAsync(probe,ct);if((int)existing.StatusCode!=207)response.EnsureSuccessStatusCode();}directories.Add(parent);}}
    public async Task<T?> Get<T>(string path,CancellationToken ct)where T:class{using var response=await http.GetAsync(path,ct);if(response.StatusCode==HttpStatusCode.NotFound)return null;response.EnsureSuccessStatusCode();byte[] body=await response.Content.ReadAsByteArrayAsync(ct);return JsonSerializer.Deserialize<T>(body,Wire.Json)??throw new CryptographicException("同步组数据无效");}
    public async Task Put<T>(string path,T value,CancellationToken ct,string? bearer=null,bool createOnly=false){using var request=new HttpRequestMessage(HttpMethod.Put,path){Content=new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(value,Wire.Json))};request.Content.Headers.ContentType=new("application/json");if(bearer is not null)request.Headers.Authorization=new("Bearer",bearer);if(createOnly)request.Headers.TryAddWithoutValidation("If-None-Match","*");using var response=await http.SendAsync(request,ct);response.EnsureSuccessStatusCode();}
    public async Task Post<T>(string path,T value,CancellationToken ct,string? bearer=null){using var request=new HttpRequestMessage(HttpMethod.Post,path){Content=new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(value,Wire.Json))};request.Content.Headers.ContentType=new("application/json");if(bearer is not null)request.Headers.Authorization=new("Bearer",bearer);using var response=await http.SendAsync(request,ct);response.EnsureSuccessStatusCode();}
    public void Dispose()=>http.Dispose();
}
public sealed class GroupStore:IDisposable
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string,SemaphoreSlim> writers=new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string,long> published=new();
    private readonly DeviceGroup group;
    private readonly GroupHttp http;
    private readonly string prefix;
    public GroupStore(DeviceGroup group){this.group=group;prefix=group.Connection.Transport=="webdav"?Groups.StoragePrefix(group):"";http=group.Connection.Transport=="relay"?new(group.Connection.Server.TrimEnd('/')+"/v2/groups/"+group.Id+"/store/","group",group.Access):new(group.Connection.Server,group.Connection.Username,group.Connection.Password);}
    private GroupBox Seal(string author,GroupFeed feed){byte[] key=Convert.FromBase64String(group.DirectoryKey),nonce=RandomNumberGenerator.GetBytes(12),plain=JsonSerializer.SerializeToUtf8Bytes(feed,Wire.Json),cipher=new byte[plain.Length+16];using var aes=new AesGcm(key,16);aes.Encrypt(nonce,plain,cipher.AsSpan(0,plain.Length),cipher.AsSpan(plain.Length),Groups.Bytes("clipbridge:group-directory:1:"+group.Id+":"+author));return new(Convert.ToBase64String(nonce),Convert.ToBase64String(cipher));}
    private GroupFeed Open(string author,GroupBox box){byte[] key=Convert.FromBase64String(group.DirectoryKey),nonce=Convert.FromBase64String(box.Nonce),cipher=Convert.FromBase64String(box.Data);if(nonce.Length!=12||cipher.Length is <16 or >200000)throw new CryptographicException();byte[] plain=new byte[cipher.Length-16];using var aes=new AesGcm(key,16);aes.Decrypt(nonce,cipher.AsSpan(0,plain.Length),cipher.AsSpan(plain.Length),plain,Groups.Bytes("clipbridge:group-directory:1:"+group.Id+":"+author));return JsonSerializer.Deserialize<GroupFeed>(plain,Wire.Json)??throw new CryptographicException();}
    public async Task<GroupFeed?> Read(string author,CancellationToken ct){Wire.Id(author);var box=await http.Get<GroupBox>(prefix+"members/"+author+".json",ct);return box is null?null:Open(author,box);}
    public async Task Publish(GroupIdentity identity,CancellationToken ct){string author=identity.Member.Id,key=group.Id+":"+author;var gate=writers.GetOrAdd(key,_=>new(1,1));await gate.WaitAsync(ct);try{await http.Ensure(prefix+"members/",ct);var feed=Groups.Feed(group,identity);if(published.GetValueOrDefault(key)>feed.Sequence)return;await http.Put(prefix+"members/"+author+".json",Seal(author,feed),ct);published[key]=feed.Sequence;group.PublishedSequence=Math.Max(group.PublishedSequence,feed.Sequence);}finally{gate.Release();}}
    public static async Task Provision(DeviceGroup group,string setupCode,CancellationToken ct){if(group.Connection.Transport=="webdav"){using var store=new GroupStore(group);return;}using var http=new GroupHttp(group.Connection.Server);await http.Post("v2/groups",new{id=group.Id,authHash=Convert.ToBase64String(SHA256.HashData(Groups.Bytes(group.Access)))},ct,setupCode);}
    public void Dispose()=>http.Dispose();
}
public sealed class GroupRendezvous:IDisposable
{
    private readonly GroupHttp http;
    private readonly string prefix;
    private readonly string token;
    public GroupRendezvous(GroupConnection connection,string locator,string token){connection.Validate();if(locator.Length!=6||locator.Any(c=>!char.IsAsciiLetterOrDigit(c)))throw new ArgumentException();this.token=token;prefix=connection.Transport=="webdav"?"clipbridge/invites/"+locator+"/":"";http=connection.Transport=="relay"?new(connection.Server.TrimEnd('/')+"/v2/invitations/"+locator+"/"):new(connection.Server,connection.Username,connection.Password);}
    public async Task Create(DeviceGroup group,string locator,GroupInviteMeta meta,CancellationToken ct){if(group.Connection.Transport=="relay"){using var client=new GroupHttp(group.Connection.Server);await client.Post("v2/groups/"+group.Id+"/invitations/"+locator,new{meta,ownerHash=Convert.ToBase64String(SHA256.HashData(Groups.Bytes(token)))},ct,group.Access);}else{if(await http.Get<GroupInviteMeta>(prefix+"meta.json",ct)is not null)throw new InvalidOperationException("邀请编号冲突，请重新生成");await http.Ensure(prefix,ct);await http.Put(prefix+"meta.json",meta,ct,createOnly:true);}}
    public Task<GroupInviteMeta?> Meta(CancellationToken ct)=>http.Get<GroupInviteMeta>(prefix+"meta.json",ct);
    public Task<T?> Read<T>(string slot,CancellationToken ct)where T:class=>http.Get<T>(prefix+slot+".json",ct);
    public async Task Write<T>(string slot,T value,CancellationToken ct){using(var current=await http.Get<JsonDocument>(prefix+slot+".json",ct)){if(current is not null){if(!System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(current.RootElement.GetRawText()),JsonSerializer.SerializeToNode(value,Wire.Json)))throw new CryptographicException("配对消息冲突，请重新生成邀请");return;}}try{await http.Put(prefix+slot+".json",value,ct,bearer:prefix.Length==0?token:null,createOnly:true);}catch(HttpRequestException e)when(e.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict){using var old=await http.Get<JsonDocument>(prefix+slot+".json",ct);if(old is null||!System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(old.RootElement.GetRawText()),JsonSerializer.SerializeToNode(value,Wire.Json)))throw new CryptographicException("配对消息冲突，请重新生成邀请");}}
    public static string Link(GroupConnection connection,string code)=>"clipbridge://group#"+Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new GroupJoinLink(connection,GroupPake.Normalize(code)),Wire.Json)).TrimEnd('=').Replace('+','-').Replace('/','_');
    public static GroupJoinLink Parse(string text){if(text.Length>8192||!text.StartsWith("clipbridge://group#",StringComparison.Ordinal))throw new ArgumentException("无效的同步组邀请");string encoded=text[(text.IndexOf('#')+1)..].Replace('-','+').Replace('_','/');encoded=encoded.PadRight((encoded.Length+3)/4*4,'=');var link=JsonSerializer.Deserialize<GroupJoinLink>(Convert.FromBase64String(encoded),Wire.Json)??throw new ArgumentException();link.Connection.Validate();return link with{Code=GroupPake.Normalize(link.Code)};}
    public void Dispose()=>http.Dispose();
}
