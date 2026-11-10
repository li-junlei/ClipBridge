using System.Security.Cryptography;
using System.Text.Json;
namespace ClipBridge;
public record GroupHello(GroupMember Member,string Group,string Name,string Root,string CommitNonce,string Signature);
public record GroupApproval(bool Accepted,string Proof);
public sealed class GroupSession:IDisposable
{
    private readonly GroupIdentity identity;
    private readonly GroupPake pake;
    private readonly GroupRendezvous signal;
    private readonly GroupInviteMeta meta;
    private readonly bool host;
    private readonly Action save;
    private readonly Action<DeviceGroup>? joined;
    private DeviceGroup? group;
    private int stage;private PakeMessage? second,third;private bool finished,joinedApplied;private readonly Dictionary<string,GroupBox> boxes=[];
    private bool approvalSent,peerApproved,groupAdded,grantSent;
    private string ownHello="",peerHello="";
    public string Code{get;}
    public string Link{get;}
    public string Sas{get;private set;}="";
    public string Status{get;private set;}="等待对方连接";
    public GroupMember? Peer{get;private set;}
    public bool Approved{get;private set;}
    public bool Ready=>stage>=4&&!Done;
    public bool Done{get;private set;}
    public long Expires=>meta.Expires;
    private string Own=>host?"a":"b";
    private string Other=>host?"b":"a";
    private GroupSession(GroupIdentity identity,GroupPake pake,GroupRendezvous signal,GroupInviteMeta meta,bool host,string code,GroupConnection connection,DeviceGroup? group,Action save,Action<DeviceGroup>? joined){this.identity=identity;this.pake=pake;this.signal=signal;this.meta=meta;this.host=host;Code=code;Link=GroupRendezvous.Link(connection,code);this.group=group;this.save=save;this.joined=joined;}
    public static async Task<GroupSession> Invite(DeviceGroup group,GroupIdentity identity,Action save,CancellationToken ct){Groups.Validate(group);if(group.Removed.Contains(identity.Member.Id)||!group.Enabled)throw new InvalidOperationException("此设备已退出或同步组已停用");string code=GroupPake.NewCode(),token=Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));var pake=new GroupPake(code,true,Commitment(identity.Member,group.Id,Groups.CertificateHash(group.Root)));var meta=new GroupInviteMeta(group.Id,Groups.CertificateHash(group.Root),DateTimeOffset.UtcNow.ToUnixTimeSeconds()+300,pake.Round1());var signal=new GroupRendezvous(group.Connection,code[..6],token);try{await signal.Create(group,code[..6],meta,ct);return new(identity,pake,signal,meta,true,code,group.Connection,group,save,null);}catch{signal.Dispose();throw;}}
    public static async Task<GroupSession> Join(GroupConnection connection,string code,GroupIdentity identity,Action<DeviceGroup> joined,CancellationToken ct){code=GroupPake.Normalize(code);var signal=new GroupRendezvous(connection,code[..6],Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));try{var meta=await signal.Meta(ct)??throw new InvalidOperationException("邀请不存在或已过期");Wire.Id(meta.Group);if(meta.Root.Length!=64||meta.Expires<=DateTimeOffset.UtcNow.ToUnixTimeSeconds()||meta.Expires>DateTimeOffset.UtcNow.ToUnixTimeSeconds()+360)throw new CryptographicException("邀请已过期或数据无效");var pake=new GroupPake(code,false,Commitment(identity.Member,meta.Group,meta.Root));await signal.Write("b1",pake.Round1(),ct);return new(identity,pake,signal,meta,false,code,connection,null,()=>{},joined);}catch{signal.Dispose();throw;}}
    private static string Commitment(GroupMember member,string group,string root)=>group+"\n"+root+"\n"+member.Id+"\n"+Convert.ToBase64String(Groups.Bytes(member.Name))+"\n"+member.SigningKey+"\n"+member.AgreementKey;
    private static byte[] HelloData(GroupHello hello,string code,string binding)=>Groups.Bytes("clipbridge:group-hello:1\n"+binding+"\n"+code[..6]+"\n"+hello.Group+"\n"+hello.Root+"\n"+hello.CommitNonce+"\n"+Convert.ToBase64String(Groups.Bytes(hello.Name))+"\n"+hello.Member.Id+"\n"+Convert.ToBase64String(Groups.Bytes(hello.Member.Name))+"\n"+hello.Member.SigningKey+"\n"+hello.Member.AgreementKey);
    private T Open<T>(string slot,GroupBox box)=>JsonSerializer.Deserialize<T>(pake.Open(slot,box),Wire.Json)??throw new CryptographicException();
    private Task Send<T>(string slot,T data,CancellationToken ct){if(!boxes.TryGetValue(slot,out var box))boxes[slot]=box=pake.Seal(slot,JsonSerializer.Serialize(data,Wire.Json));return signal.Write(slot,box,ct);}
    public void Approve(){if(!Ready)throw new InvalidOperationException("请等待安全码出现后再确认");Approved=true;Status="已确认，等待对方确认";}
    public async Task Step(CancellationToken ct){
        if(Done)return;if(DateTimeOffset.UtcNow.ToUnixTimeSeconds()>=meta.Expires)throw new InvalidOperationException("邀请已过期，请重新生成");
        if(stage==0){var first=host?await signal.Read<PakeMessage>("b1",ct):meta.First;if(first is null)return;await signal.Write(Own+"2",second??=pake.Round2(first),ct);stage=1;Status="正在验证配对口令";}
        if(stage==1){var second=await signal.Read<PakeMessage>(Other+"2",ct);if(second is null)return;await signal.Write(Own+"3",third??=pake.Round3(second),ct);stage=2;}
        if(stage==2){var third=await signal.Read<PakeMessage>(Other+"3",ct);if(third is null)return;if(!finished){pake.Finish(third);finished=true;}var hello=new GroupHello(identity.Member,meta.Group,host?group!.Name:"",meta.Root,pake.CommitNonce,"");hello=hello with{Signature=identity.Sign(HelloData(hello,Code,pake.Binding))};if(ownHello.Length==0)ownHello=JsonSerializer.Serialize(hello,Wire.Json);await Send(Own+"hello",JsonSerializer.Deserialize<GroupHello>(ownHello,Wire.Json),ct);stage=3;}
        if(stage==3){var box=await signal.Read<GroupBox>(Other+"hello",ct);if(box is null)return;peerHello=pake.Open(Other+"hello",box);var hello=JsonSerializer.Deserialize<GroupHello>(peerHello,Wire.Json)??throw new CryptographicException();Groups.ValidateMember(hello.Member);if(hello.Group!=meta.Group||hello.Root!=meta.Root||hello.Member.Id==identity.Member.Id||!pake.VerifyCommitment(Commitment(hello.Member,hello.Group,hello.Root),hello.CommitNonce)||!Groups.Verify(hello.Member,HelloData(hello,Code,pake.Binding),hello.Signature))throw new CryptographicException("设备身份验证失败");Peer=hello.Member;if(host&&(group!.Members.ContainsKey(Peer.Id)&&!group.Removed.Contains(Peer.Id)))throw new InvalidOperationException("此设备已在组内或曾被移出，请勿重复加入");Sas=pake.Sas(host?ownHello:peerHello,host?peerHello:ownHello);stage=4;Status="请核对两端的安全码并分别确认";}
        if(stage==4){string a=host?ownHello:peerHello,b=host?peerHello:ownHello;
            if(Approved&&!approvalSent){await Send(Own+"approval",new GroupApproval(true,pake.IdentityProof(identity,Peer!,a,b,Own)),ct);approvalSent=true;}
            if(!peerApproved){var box=await signal.Read<GroupBox>(Other+"approval",ct);if(box is not null){var approval=Open<GroupApproval>(Other+"approval",box);if(!approval.Accepted||!CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(approval.Proof),Convert.FromBase64String(pake.IdentityProof(identity,Peer!,a,b,Other))))throw new CryptographicException("设备密钥确认失败");peerApproved=true;}}
            if(!Approved||!peerApproved)return;
            if(host){if(!groupAdded){Groups.Add(group!,identity,Peer!);groupAdded=true;}save();if(!grantSent){using var store=new GroupStore(group!);await store.Publish(identity,ct);await Send("grant",Groups.Export(group!),ct);grantSent=true;}stage=5;Status="已授权，等待新设备完成入组";}
            else{var box=await signal.Read<GroupBox>("grant",ct);if(box is null)return;var received=Open<DeviceGroup>("grant",box);Groups.Validate(received);if(received.Id!=meta.Group||Groups.CertificateHash(received.Root)!=meta.Root||!received.Members.TryGetValue(identity.Member.Id,out var own)||own.Member!=identity.Member||!received.Members.TryGetValue(Peer!.Id,out var inviting)||inviting.Member!=Peer||received.Removed.Contains(identity.Member.Id)||received.Removed.Contains(Peer.Id))throw new CryptographicException("入组授权与当前设备不一致");using(var history=new GroupStore(received)){var prior=await history.Read(identity.Member.Id,ct);if(prior is not null){if(prior.Group!=received.Id||prior.Author!=identity.Member.Id||!Groups.Verify(identity.Member,Groups.FeedData(prior),prior.Signature))throw new CryptographicException("本机历史成员清单无效");if(prior.Sequence>(received.Feeds.GetValueOrDefault(identity.Member.Id)?.Sequence??0))received.Feeds[identity.Member.Id]=prior;}}received.OwnCertificates=[];received.OwnRemoved=[];received.Sequence=1;if(!joinedApplied){joined!(received);joinedApplied=true;}group=received;await Send("done",new GroupApproval(true,""),ct);Done=true;Status="已加入同步组，正在连接其他成员";}
        }
        if(stage==5){var box=await signal.Read<GroupBox>("done",ct);if(box is not null&&Open<GroupApproval>("done",box).Accepted){Done=true;Status="新设备已加入同步组";}}
    }
    public void Dispose(){signal.Dispose();pake.Dispose();}
}
