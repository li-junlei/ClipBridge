using System.Net;
namespace ClipBridge;
public sealed partial class AttachmentClient
{
    private string BundlePath(string device,string id)=>Path(device,id+"-0").Replace(id+"-0.bin",id+".cbx");
    public async Task PutBundle(string source,AttachmentOffer offer,CancellationToken ct){
        Attachments.Validate(offer);await Ensure(config.Device,true,ct);
        using var body=new BundleContent(config,source,offer);body.Headers.ContentType=new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        using var response=await http.PutAsync(BundlePath(config.Device,offer.Id),body,ct);response.EnsureSuccessStatusCode();
    }
    public async Task GetBundle(AttachmentOffer offer,string destination,CancellationToken ct){
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromMinutes(10));ct=deadline.Token;
        Attachments.Validate(offer);using var response=await http.GetAsync(BundlePath(config.Peer,offer.Id),HttpCompletionOption.ResponseHeadersRead,ct);response.EnsureSuccessStatusCode();
        long expected=offer.Size+28L*offer.Chunks;if(response.Content.Headers.ContentLength is long length&&length!=expected)throw new IOException("Encrypted file size mismatch");
        await using var input=await response.Content.ReadAsStreamAsync(ct);await using var output=new FileStream(destination,FileMode.Create,FileAccess.Write,FileShare.None,Attachments.ChunkSize,true);
        for(int part=0;part<offer.Chunks;part++){int size=(int)Math.Min(Attachments.ChunkSize,offer.Size-(long)part*Attachments.ChunkSize);byte[] cipher=new byte[size+28];await input.ReadExactlyAsync(cipher,ct);byte[] plain=Attachments.Decrypt(config,offer.Id,part,cipher);await output.WriteAsync(plain,ct);}
        if(await input.ReadAsync(new byte[1],ct)!=0)throw new IOException("Trailing encrypted file data");await output.FlushAsync(ct);
    }
    private sealed class BundleContent(ClientConfig config,string source,AttachmentOffer offer):HttpContent
    {
        protected override bool TryComputeLength(out long length){length=offer.Size+28L*offer.Chunks;return true;}
        protected override Task SerializeToStreamAsync(Stream stream,TransportContext? context)=>SerializeToStreamAsync(stream,context,CancellationToken.None);
        protected override async Task SerializeToStreamAsync(Stream stream,TransportContext? context,CancellationToken ct){
            await using var input=File.OpenRead(source);if(input.Length!=offer.Size)throw new IOException("Source changed");
            for(int part=0;part<offer.Chunks;part++){int size=(int)Math.Min(Attachments.ChunkSize,offer.Size-input.Position);byte[] plain=new byte[size];await input.ReadExactlyAsync(plain,ct);await stream.WriteAsync(Attachments.Encrypt(config,offer.Id,part,plain),ct);}
        }
    }
}
