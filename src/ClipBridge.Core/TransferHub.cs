using System.Security.Cryptography;
namespace ClipBridge;

public sealed class BridgeProfile
{
    public required ClientConfig Config{get;set;}
    public bool Enabled{get;set;}=true;
    public SyncState State{get;set;}=new();
    public string GroupKey{get;set;}="";public string GroupId{get;set;}="";public string GroupPeer{get;set;}="";public string Name{get;set;}="";
    public string PeerIdentity{get;set;}="";
    public long LastSync{get;set;}
    public string Status{get;set;}="等待连接";
    public string Group=>GroupKey.Length>0?"trusted:"+GroupKey:PeerIdentity.Length==32?PeerIdentity:Config.Pair;
    public string Title=>Name.Length==0?Config.Transport+" · "+Config.Pair[..6]:Name;
}
public sealed class OutgoingAttachment
{
    public required AttachmentOffer Offer{get;set;}
    public required string Path{get;set;}
    public List<string> Pairs{get;set;}=[];
    public string Target{get;set;}="";
    public Dictionary<string,int> Uploaded{get;set;}=[];
    public Dictionary<string,int> Cleaned{get;set;}=[];
    public int Route{get;set;}
    public long OfferedAt{get;set;}
    public string Phase{get;set;}="waiting";
}
public sealed class IncomingAttachment
{
    public required AttachmentOffer Offer{get;set;}
    public List<string> Pairs{get;set;}=[];
    public string Phase{get;set;}="waiting";
    public string Path{get;set;}="";
    public string SourcePair{get;set;}="";public string Destination{get;set;}="";public int Downloaded{get;set;}
}
public sealed class AttachmentState
{
    public List<OutgoingAttachment> Outgoing{get;set;}=[];
    public List<IncomingAttachment> Incoming{get;set;}=[];
}
// One hub per installation. A transfer ID remains the same when switching routes.
public sealed class TransferHub
{
    private readonly Func<List<BridgeProfile>> profiles;
    private readonly AttachmentState state;
    private readonly string cache,identity;
    private readonly Action save;private readonly Func<long> clock;private readonly Func<int> attachmentInterval;private int lastInterval;
    private readonly Dictionary<string,AttachmentClient> clients=[];
    private readonly Dictionary<string,AttachmentIndex> remote=[];
    private readonly Dictionary<string,long> published=[];private readonly Dictionary<string,long> nextPoll=[],nextWrite=[],nextTransfer=[];
    private readonly SemaphoreSlim gate=new(1,1);
    private bool restored;private readonly HashSet<string> errors=[];
    public Action<IncomingAttachment>? Requested{get;set;}
    public Func<IncomingAttachment,Task>? Received{get;set;}
    public Action<string>? Notice{get;set;}
    public TransferHub(Func<List<BridgeProfile>> profiles,AttachmentState state,string cache,string identity,Action save,Func<long>? clock=null,Func<int>? attachmentInterval=null){this.attachmentInterval=attachmentInterval??(()=>3);this.clock=clock??(()=>DateTimeOffset.UtcNow.ToUnixTimeSeconds());this.profiles=profiles;this.state=state;this.cache=cache;this.identity=identity;this.save=save;Directory.CreateDirectory(cache);}
    public async Task Stage(string source,string kind,List<string> pairs,CancellationToken ct){
        if(new FileInfo(source).Length>Attachments.MaxSize)throw new ArgumentException("单文件上限 100 MB，请改用网盘或其他大文件传输方式。");
        if(Directory.EnumerateFiles(cache).Sum(f=>new FileInfo(f).Length)+new FileInfo(source).Length>512L*1024*1024)throw new ArgumentException("附件暂存已接近 512 MB，请先完成或取消现有传输。");
        string id=Guid.NewGuid().ToString("N"),path=System.IO.Path.Combine(cache,id+".send");
        try{
            long size=0;using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using(var input=File.OpenRead(source))using(var output=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None,Attachments.ChunkSize,true)){
                var buffer=new byte[Attachments.ChunkSize];int n;while((n=await input.ReadAsync(buffer,ct))>0){size+=n;if(size>Attachments.MaxSize)throw new ArgumentException("文件超过 100 MB，请使用其他传输方式。");hash.AppendData(buffer,0,n);await output.WriteAsync(buffer.AsMemory(0,n),ct);}await output.FlushAsync(ct);
            }
            var offer=new AttachmentOffer(id,Attachments.SafeName(System.IO.Path.GetFileName(source)),kind=="image"?"image/png":"application/octet-stream",kind,size,Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),(int)Math.Max(1,(size+Attachments.ChunkSize-1)/Attachments.ChunkSize),DateTimeOffset.UtcNow.ToUnixTimeSeconds()+3600,"offered",identity);Attachments.Validate(offer);
            await gate.WaitAsync(ct);try{if(state.Outgoing.Count(j=>j.Phase is not("done" or "rejected" or "cancelled"))>=20)throw new ArgumentException("待发送任务过多，请先完成或取消现有传输。");state.Outgoing.Add(new(){Offer=offer,Path=path,Pairs=pairs,Target=profiles().FirstOrDefault(p=>pairs.Contains(p.Config.Pair))?.Group??""});save();published.Clear();}finally{gate.Release();}
        }catch{if(File.Exists(path))File.Delete(path);throw;}
    }
    public async Task Decide(string id,bool accept){await gate.WaitAsync();try{var item=state.Incoming.FirstOrDefault(x=>x.Offer.Id==id);if(item is null)return;if(item.Offer.Expires<=DateTimeOffset.UtcNow.ToUnixTimeSeconds()){accept=false;Notice?.Invoke("文件请求已过期，请让对方重新发送。");}item.Phase=accept?"accepted":"rejected";save();published.Clear();}finally{gate.Release();}}
    public async Task Complete(IncomingAttachment item,bool accepted){await gate.WaitAsync();try{item.Phase=accepted?"done":"rejected";if(accepted){var profile=profiles().FirstOrDefault(p=>p.Config.Pair==item.SourcePair);if(profile is not null)profile.LastSync=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();}save();published.Clear();if(File.Exists(item.Path))File.Delete(item.Path);}finally{gate.Release();}}
    public void RemoveRoute(string pair){if(clients.Remove(pair,out var client))client.Dispose();remote.Remove(pair);published.Remove(pair);foreach(var job in state.Outgoing){job.Pairs.Remove(pair);if(job.Pairs.Count==0){job.Phase="cancelled";job.Offer=job.Offer with{Status="cancelled"};if(File.Exists(job.Path))File.Delete(job.Path);}}foreach(var item in state.Incoming){item.Pairs.Remove(pair);foreach(var alternate in profiles().Where(p=>p.Enabled&&p.PeerIdentity==item.Offer.Sender))if(!item.Pairs.Contains(alternate.Config.Pair))item.Pairs.Add(alternate.Config.Pair);if(item.Pairs.Count==0 && item.Phase!="done"){item.Phase="rejected";if(File.Exists(item.Path))File.Delete(item.Path);}}save();}
    public bool Busy=>state.Outgoing.Any(j=>j.Phase is not("done" or "rejected" or "cancelled")&&j.Uploaded.Values.Any(n=>n>0&&n<j.Offer.Chunks))||state.Incoming.Any(i=>i.Phase=="accepted"&&i.Downloaded>0&&i.Downloaded<i.Offer.Chunks);
    public void CancelAll(){foreach(var job in state.Outgoing.Where(x=>x.Phase is not("done" or "rejected"))) {job.Phase="cancelled";job.Offer=job.Offer with{Status="cancelled"};}save();published.Clear();}
    public async Task Tick(CancellationToken ct){
        await gate.WaitAsync(ct);var callbacks=new List<IncomingAttachment>();var completed=new List<IncomingAttachment>();try{
            if(!restored){callbacks.AddRange(state.Incoming.Where(i=>i.Phase=="waiting"));completed.AddRange(state.Incoming.Where(i=>i.Phase=="preview" && File.Exists(i.Path)));restored=true;}
            int interval=Math.Clamp(attachmentInterval(),1,300);if(interval!=lastInterval){nextPoll.Clear();lastInterval=interval;}long now=clock();foreach(var expired in state.Incoming.Where(i=>i.Offer.Expires<=now && i.Phase is "waiting" or "accepted")){expired.Phase="rejected";if(File.Exists(expired.Path))File.Delete(expired.Path);published.Clear();}
            var active=profiles().Where(x=>x.Enabled).ToList();
            foreach(var p in active){if(!p.Enabled)continue;string pair=p.Config.Pair;try{
                if(!clients.TryGetValue(pair,out var client))clients[pair]=client=new AttachmentClient(p.Config);
                if(now<nextPoll.GetValueOrDefault(pair))continue;nextPoll[pair]=now+interval;var index=await client.GetIndex(ct);if(!p.Enabled)continue;errors.Remove(pair);if(index is not null){remote[pair]=index;
                    foreach(var offer in index.Offers.Where(o=>o.Expires>now)){if(p.PeerIdentity.Length==32&&offer.Sender!=p.PeerIdentity)continue;
                        var item=state.Incoming.FirstOrDefault(i=>i.Offer.Id==offer.Id && i.Offer.Sender==offer.Sender);
                        if(item is null){if(state.Incoming.Count(i=>i.Phase is not("done" or "rejected"))>=32)continue;item=new(){Offer=offer,Phase=offer.Kind=="image"?"accepted":"waiting"};state.Incoming.Add(item);callbacks.Add(item);}
                        if(!item.Pairs.Contains(pair))item.Pairs.Add(pair);if(offer.Status=="cancelled" && item.Phase is not("done" or "rejected")){item.Phase="rejected";published.Clear();}
                    }
                }
            }catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}catch{remote.Remove(pair);nextPoll[pair]=now+(p.Config.Transport=="webdav"?300:10);p.Status=p.Config.Transport=="webdav"?"WebDAV 暂不可用，5 分钟后重试":"附件通道不可用";if(errors.Add(pair))Notice?.Invoke(p.Config.Transport=="webdav"?"WebDAV 附件连接失败，请检查权限或服务商限频；稍后重试。":"附件通道连接失败，请检查或更新专用中转。");}}
            foreach(var job in state.Outgoing.Where(j=>j.Phase is not("done" or "rejected" or "cancelled")).ToList()){
                if(job.Offer.Expires<=now){job.Phase="cancelled";job.Offer=job.Offer with{Status="cancelled"};published.Clear();continue;}
                var original=profiles().FirstOrDefault(p=>job.Pairs.Contains(p.Config.Pair));if(job.Target.Length==0 || original is not null && job.Target==original.Config.Pair && original.PeerIdentity.Length==32)job.Target=original?.Group??job.Target;
                foreach(var added in active.Where(p=>p.Group==job.Target))if(!job.Pairs.Contains(added.Config.Pair))job.Pairs.Add(added.Config.Pair);
                var routes=active.Where(p=>p.Enabled&&job.Pairs.Contains(p.Config.Pair)).ToList();if(routes.Count==0)continue;
                var decisions=routes.Where(p=>remote.ContainsKey(p.Config.Pair)).SelectMany(p=>remote[p.Config.Pair].Decisions).Where(d=>d.Id==job.Offer.Id).ToList();
                if(decisions.Any(d=>d.Status=="done")){job.Phase="done";if(File.Exists(job.Path))File.Delete(job.Path);Notice?.Invoke("对方已接收："+job.Offer.Name);continue;}
                if(decisions.Any(d=>d.Status=="rejected")){job.Phase="rejected";if(File.Exists(job.Path))File.Delete(job.Path);Notice?.Invoke("对方已拒绝："+job.Offer.Name);continue;}
                bool accepted=job.Offer.Kind=="image" || decisions.Any(d=>d.Status=="accepted");
                if(!accepted){if(job.OfferedAt==0)job.OfferedAt=now;else if(routes.Count>1&&now-job.OfferedAt>=(routes[job.Route%routes.Count].Config.Transport=="webdav"?120:15)){job.Route=(job.Route+1)%routes.Count;job.OfferedAt=now;published.Clear();}continue;}
                var route=routes[job.Route%routes.Count];string pair=route.Config.Pair;if(now<nextTransfer.GetValueOrDefault(pair))continue;
                try{var client=clients[pair];int part=job.Uploaded.GetValueOrDefault(pair);using var file=File.OpenRead(job.Path);file.Position=(long)part*Attachments.ChunkSize;
                    if(route.Config.Transport=="webdav"&&part<job.Offer.Chunks){await client.PutBundle(job.Path,job.Offer,ct);job.Uploaded[pair]=job.Offer.Chunks;}
                    else for(int batch=0;batch<32 && part<job.Offer.Chunks && route.Enabled && job.Phase!="cancelled";batch++,part++){
                        int length=(int)Math.Min(Attachments.ChunkSize,job.Offer.Size-file.Position);byte[] plain=new byte[length];await file.ReadExactlyAsync(plain,ct);await client.PutChunk(job.Offer.Id,part,plain,ct);job.Uploaded[pair]=part+1;
                        route.Status=$"发送 {job.Offer.Name} · {(part+1)*100/job.Offer.Chunks}%";
                    }
                    if(job.Phase!="cancelled" && job.Offer.Status!="ready" && job.Uploaded.GetValueOrDefault(pair)==job.Offer.Chunks){job.Offer=job.Offer with{Status="ready"};job.Phase="ready";route.LastSync=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();published.Clear();}
                }catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}catch{nextTransfer[pair]=now+(route.Config.Transport=="webdav"?300:10);job.Route=(job.Route+1)%routes.Count;job.Offer=job.Offer with{Status="offered"};published.Clear();}
            }
            foreach(var item in state.Incoming.Where(i=>i.Phase=="accepted" && i.Offer.Expires>now).ToList()){
                foreach(var p in active.Where(p=>item.Pairs.Contains(p.Config.Pair))){if(now<nextTransfer.GetValueOrDefault(p.Config.Pair))continue;if(!remote.TryGetValue(p.Config.Pair,out var index)||!index.Offers.Any(o=>o.Id==item.Offer.Id && o.Status=="ready"))continue;
                    try{
                        if(item.Downloaded==0 && Directory.EnumerateFiles(cache).Sum(f=>new FileInfo(f).Length)+item.Offer.Size>512L*1024*1024)continue;
                        string path=System.IO.Path.Combine(cache,item.Offer.Sender+"-"+item.Offer.Id+".receive");
                        // A failed route starts this local temporary file again, never appending unverified data.
                        if(item.Path!=path){item.Path=path;item.Downloaded=0;}using(var file=new FileStream(path,item.Downloaded==0?FileMode.Create:FileMode.OpenOrCreate,FileAccess.Write,FileShare.None,Attachments.ChunkSize,true)){
                            file.Position=(long)item.Downloaded*Attachments.ChunkSize;
                            if(p.Config.Transport=="webdav"){file.Close();await clients[p.Config.Pair].GetBundle(item.Offer,path,ct);item.Downloaded=item.Offer.Chunks;}
                            else for(int batch=0;batch<32 && item.Downloaded<item.Offer.Chunks && p.Enabled;batch++){
                                byte[] data=await clients[p.Config.Pair].GetChunk(item.Offer.Id,item.Downloaded,ct);int expected=(int)Math.Min(Attachments.ChunkSize,item.Offer.Size-(long)item.Downloaded*Attachments.ChunkSize);if(data.Length!=expected)throw new CryptographicException();await file.WriteAsync(data,ct);item.Downloaded++;p.Status=$"接收 {item.Offer.Name} · {item.Downloaded*100/item.Offer.Chunks}%";
                            }if(p.Config.Transport!="webdav")await file.FlushAsync(ct);
                        }
                        if(p.Enabled && item.Phase=="accepted" && item.Downloaded==item.Offer.Chunks){using var input=File.OpenRead(path);if(input.Length!=item.Offer.Size || Convert.ToHexString(await SHA256.HashDataAsync(input,ct)).ToLowerInvariant()!=item.Offer.Hash)throw new CryptographicException();item.Phase="preview";item.SourcePair=p.Config.Pair;completed.Add(item);}
                        break;
                    }catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}catch{nextTransfer[p.Config.Pair]=now+(p.Config.Transport=="webdav"?300:10);item.Downloaded=0;if(File.Exists(item.Path))File.Delete(item.Path);}
                }
            }
            foreach(var p in active){if(!p.Enabled)continue;string pair=p.Config.Pair;if(!clients.TryGetValue(pair,out var client))continue;if(now<nextWrite.GetValueOrDefault(pair)||published.GetValueOrDefault(pair)>now-(p.Config.Transport=="webdav"?900:30))continue;
                try{var offers=state.Outgoing.Where(j=>j.Pairs.Contains(pair)&&j.Offer.Expires>now&&j.Phase is not("done" or "rejected")).OrderBy(j=>j.Phase=="cancelled"?1:0).Take(32).Select(j=>j.Offer.Status=="ready" && j.Uploaded.GetValueOrDefault(pair)<j.Offer.Chunks?j.Offer with{Status="offered"}:j.Offer).ToList();
                    var receipts=state.Incoming.Where(i=>i.Pairs.Contains(pair)&&i.Offer.Expires>now&&i.Phase!="waiting").TakeLast(128).Select(i=>new AttachmentDecision(i.Offer.Id,i.Phase is "done" or "rejected"?i.Phase:"accepted")).ToList();
                    await client.PutIndex(new(offers,receipts),ct);published[pair]=now;
                }catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}catch{nextWrite[pair]=now+(p.Config.Transport=="webdav"?300:10);p.Status="附件通知上传失败，稍后重试";}
            }
            foreach(var job in state.Outgoing.Where(j=>j.Phase is "done" or "rejected" or "cancelled")){
                foreach(var route in job.Uploaded.Keys.ToArray()){if(!clients.TryGetValue(route,out var client)||!active.Any(p=>p.Enabled&&p.Config.Pair==route))continue;int cleaned=job.Cleaned.GetValueOrDefault(route);try{for(int batch=0;batch<32&&cleaned<job.Uploaded[route];batch++,cleaned++){await client.DeleteChunk(job.Offer.Id,cleaned,ct);job.Cleaned[route]=cleaned+1;}}catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}catch{}}
            }
            state.Incoming.RemoveAll(i=>i.Offer.Expires<now-86400 && i.Phase is "done" or "rejected");foreach(var old in state.Outgoing.Where(j=>j.Offer.Expires<now-86400 || j.Phase=="cancelled")){try{if(File.Exists(old.Path))File.Delete(old.Path);}catch(IOException){}}state.Outgoing.RemoveAll(j=>j.Offer.Expires<now-86400);save();
        }finally{gate.Release();}
        foreach(var item in callbacks.DistinctBy(i=>i.Offer.Id))if(item.Phase=="waiting" && profiles().Any(p=>p.Enabled&&item.Pairs.Contains(p.Config.Pair)) && item.Offer.Kind=="file")Requested?.Invoke(item);
        foreach(var item in completed)if(item.Phase=="preview" && profiles().Any(p=>p.Enabled&&item.Pairs.Contains(p.Config.Pair)) && Received is not null)await Received(item);
    }
}
