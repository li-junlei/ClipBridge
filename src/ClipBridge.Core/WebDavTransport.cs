using System.Net;
using System.Net.Http.Json;
namespace ClipBridge;
internal sealed class WebDavTransport(HttpClient http,ClientConfig config)
{
    private readonly SemaphoreSlim gate=new(1,1);
    private bool ready;
    private string DirectoryPath=>"clipbridge/"+config.Pair+"/";
    private async Task Ensure(CancellationToken ct){
        await gate.WaitAsync(ct);try{if(ready)return;
            foreach(var path in new[]{"clipbridge/",DirectoryPath}){using var request=new HttpRequestMessage(new HttpMethod("MKCOL"),path);using var response=await http.SendAsync(request,ct);if(response.StatusCode!=HttpStatusCode.MethodNotAllowed)response.EnsureSuccessStatusCode();}
            ready=true;
        }finally{gate.Release();}
    }
    public async Task Put(bool identity,Envelope e,CancellationToken ct){
        await Ensure(ct);
        using var body=new ByteArrayContent(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(e,Wire.Json)); body.Headers.ContentType=new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        using var response=await http.PutAsync(DirectoryPath+config.Device+(identity?".device.json":".clip.json"),body,ct);
        if(response.StatusCode==HttpStatusCode.NotFound || response.StatusCode==HttpStatusCode.Conflict)ready=false;
        response.EnsureSuccessStatusCode();
    }
    public async Task<Envelope?> Get(bool identity,CancellationToken ct){
        using var response=await http.GetAsync(DirectoryPath+config.Peer+(identity?".device.json":".clip.json"),ct);
        if(response.StatusCode==HttpStatusCode.NotFound)return null;response.EnsureSuccessStatusCode();
        var e=await response.Content.ReadFromJsonAsync<Envelope>(Wire.Json,ct)??throw new InvalidDataException();Wire.Validate(e);return e;
    }
}
