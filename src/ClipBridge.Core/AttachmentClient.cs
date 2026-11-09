using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
namespace ClipBridge;

public sealed class AttachmentClient:IDisposable
{
    private readonly ClientConfig config;
    private readonly HttpClient http;
    private readonly SemaphoreSlim collections=new(1,1);
    private readonly HashSet<string> initialized=[];private bool capable;
    private async Task Capability(CancellationToken ct){if(config.Transport=="webdav"||capable)return;using var response=await http.GetAsync("v1/setup/info",ct);response.EnsureSuccessStatusCode();using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));if(!json.RootElement.TryGetProperty("attachments",out var value)||value.GetInt32()!=1)throw new NotSupportedException("请更新专用中转以启用附件。");capable=true;}

    public AttachmentClient(ClientConfig c){c.Validate();config=c;http=new(new HttpClientHandler{AllowAutoRedirect=false}){BaseAddress=new Uri(c.Server.TrimEnd('/')+"/"),Timeout=TimeSpan.FromSeconds(45),MaxResponseContentBufferSize=600000};http.DefaultRequestHeaders.Authorization=c.Transport=="webdav"?new("Basic",Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(c.Username+":"+c.Password))):new("Bearer",c.Token);http.DefaultRequestHeaders.CacheControl=new(){NoCache=true,NoStore=true};}
    private string Path(string device,string key)=>config.Transport=="webdav"?"clipbridge/"+config.Pair+"/"+(key=="index"?device+".transfers.json":"attachments/"+device+"/"+key+".bin"):"v1/objects/"+device+"/"+key;
    private async Task Ensure(string device,bool blob,CancellationToken ct){if(config.Transport!="webdav")return;await collections.WaitAsync(ct);try{foreach(var path in new[]{"clipbridge/","clipbridge/"+config.Pair+"/",blob?"clipbridge/"+config.Pair+"/attachments/":"",blob?"clipbridge/"+config.Pair+"/attachments/"+device+"/":""}){if(path.Length==0||initialized.Contains(path))continue;using var request=new HttpRequestMessage(new HttpMethod("MKCOL"),path);using var response=await http.SendAsync(request,ct);if(response.StatusCode!=HttpStatusCode.MethodNotAllowed)response.EnsureSuccessStatusCode();initialized.Add(path);}}finally{collections.Release();}}
    public async Task PutIndex(AttachmentIndex index,CancellationToken ct){await Capability(ct);await Put("index",JsonSerializer.SerializeToUtf8Bytes(Attachments.SealIndex(config,index),Wire.Json),ct);}
    public async Task<AttachmentIndex?> GetIndex(CancellationToken ct){using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromSeconds(10));await Capability(deadline.Token);var bytes=await Get(config.Peer,"index",deadline.Token);if(bytes is null)return null;var e=JsonSerializer.Deserialize<Envelope>(bytes,Wire.Json)??throw new InvalidDataException();return Attachments.OpenIndex(config,e);}
    public async Task PutChunk(string id,int part,byte[] plain,CancellationToken ct)=>await Put(id+"-"+part,Attachments.Encrypt(config,id,part,plain),ct);
    public async Task<byte[]> GetChunk(string id,int part,CancellationToken ct)=>Attachments.Decrypt(config,id,part,await Get(config.Peer,id+"-"+part,ct)??throw new IOException("Attachment not ready"));
    private async Task Put(string key,byte[] bytes,CancellationToken ct){await Ensure(config.Device,key!="index",ct);using var body=new ByteArrayContent(bytes);using var response=await http.PutAsync(Path(config.Device,key),body,ct);response.EnsureSuccessStatusCode();}
    private async Task<byte[]?> Get(string device,string key,CancellationToken ct){using var response=await http.GetAsync(Path(device,key),ct);if(response.StatusCode==HttpStatusCode.NotFound)return null;response.EnsureSuccessStatusCode();return await response.Content.ReadAsByteArrayAsync(ct);}
    public async Task DeleteChunk(string id,int part,CancellationToken ct){using var response=await http.DeleteAsync(Path(config.Device,id+"-"+part),ct);if(response.StatusCode!=HttpStatusCode.NotFound)response.EnsureSuccessStatusCode();}
    public void Dispose()=>http.Dispose();
}
