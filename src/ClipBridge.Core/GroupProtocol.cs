using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace ClipBridge;

public sealed record GroupMember(string Id,string Name,string SigningKey,string AgreementKey);
public sealed record GroupCertificate(string Group,string Issuer,GroupMember Member,string Signature,string Admission="",string Previous="");
public sealed record GroupFeed(string Group,string Author,long Sequence,List<GroupCertificate> Certificates,List<string> Removed,string Signature);
public sealed class GroupConnection
{
    public string Transport{get;set;}="relay";
    public string Server{get;set;}="";
    public string Username{get;set;}="";
    public string Password{get;set;}="";
    public void Validate(){if(Transport is not("relay" or "webdav"))throw new ArgumentException("未知连接类型");var u=new Uri(Server);if(u.Scheme!="https"&&!(u.Scheme=="http"&&u.IsLoopback))throw new ArgumentException("请使用 HTTPS 地址");if(u.UserInfo.Length+u.Query.Length+u.Fragment.Length!=0||Transport=="relay"&&u.AbsolutePath!="/")throw new ArgumentException("连接地址无效");if(Transport=="webdav"&&(Username.Length is <1 or >256||Password.Length is <1 or >1024||Username.Contains(':')||Username.Any(char.IsControl)||Password.Any(char.IsControl)))throw new ArgumentException("WebDAV 凭据无效");}
}
public sealed class GroupIdentity
{
    public required GroupMember Member{get;set;}
    public required string SigningPrivate{get;set;}
    public required string AgreementPrivate{get;set;}
    public static GroupIdentity Create(string id,string name){Wire.Id(id);using var signing=ECDsa.Create(ECCurve.NamedCurves.nistP256);using var agreement=ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);var result=new GroupIdentity{Member=new(id,name,Convert.ToBase64String(signing.ExportSubjectPublicKeyInfo()),Convert.ToBase64String(agreement.ExportSubjectPublicKeyInfo())),SigningPrivate=Convert.ToBase64String(signing.ExportPkcs8PrivateKey()),AgreementPrivate=Convert.ToBase64String(agreement.ExportPkcs8PrivateKey())};Groups.ValidateMember(result.Member);return result;}
    public string Sign(byte[] data){using var key=ECDsa.Create();key.ImportPkcs8PrivateKey(Convert.FromBase64String(SigningPrivate),out _);return Convert.ToBase64String(key.SignData(data,HashAlgorithmName.SHA256,DSASignatureFormat.Rfc3279DerSequence));}
}
public sealed class DeviceGroup
{
    public string Id{get;set;}="";
    public string Name{get;set;}="";
    public int StorageVersion{get;set;}=1;
    public long LastSync{get;set;}
    public GroupConnection Connection{get;set;}=new();
    public string Access{get;set;}="";public string DirectoryKey{get;set;}="";
    public required GroupCertificate Root{get;set;}
    public Dictionary<string,GroupCertificate> Members{get;set;}=[];
    public Dictionary<string,GroupFeed> Feeds{get;set;}=[];
    public HashSet<string> Removed{get;set;}=[];
    public HashSet<string> Revoked{get;set;}=[];
    public List<GroupCertificate> OwnCertificates{get;set;}=[];
    public List<string> OwnRemoved{get;set;}=[];
    public long Sequence{get;set;}=1;
    public bool Enabled{get;set;}=true;
    public HashSet<string> SuppressedPeers{get;set;}=[];public long PublishRetryAfter{get;set;}public long PublishedSequence{get;set;}public long CheckedAt{get;set;}
    public string Status{get;set;}="等待连接";
}
public static class Groups
{
    public const int MaxMembers=8;public const int MaxHistory=32;
    public static byte[] Bytes(string text)=>Encoding.UTF8.GetBytes(text);
    public static string Hash(byte[] data)=>Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    private static string B64(string s)=>Convert.ToBase64String(Bytes(s));
    public static void ValidateMember(GroupMember member){Wire.Id(member.Id);if(string.IsNullOrWhiteSpace(member.Name)||member.Name.Length>80||member.Name.Any(char.IsControl))throw new ArgumentException("设备名称无效");using var s=ECDsa.Create();s.ImportSubjectPublicKeyInfo(Convert.FromBase64String(member.SigningKey),out int n);if(s.KeySize!=256||n!=Convert.FromBase64String(member.SigningKey).Length)throw new CryptographicException();using var a=ECDiffieHellman.Create();a.ImportSubjectPublicKeyInfo(Convert.FromBase64String(member.AgreementKey),out n);if(a.KeySize!=256||n!=Convert.FromBase64String(member.AgreementKey).Length)throw new CryptographicException();}
    public static byte[] CertificateData(GroupCertificate c)=>Bytes($"clipbridge:group-member:1\n{c.Group}\n{c.Issuer}\n{c.Member.Id}\n{B64(c.Member.Name)}\n{c.Member.SigningKey}\n{c.Member.AgreementKey}"+(c.Admission.Length==0?"":$"\nclipbridge:readmission:1\n{c.Admission}\n{c.Previous}"));
    public static string CertificateHash(GroupCertificate c)=>Hash(Bytes(Convert.ToBase64String(CertificateData(c))+"\n"+c.Signature));
    public static bool Verify(GroupMember signer,byte[] data,string signature){try{using var key=ECDsa.Create();key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(signer.SigningKey),out _);return key.VerifyData(data,Convert.FromBase64String(signature),HashAlgorithmName.SHA256,DSASignatureFormat.Rfc3279DerSequence);}catch(ArgumentException){return false;}catch(CryptographicException){return false;}catch(FormatException){return false;}}
    public static GroupCertificate Certify(string group,GroupIdentity issuer,GroupMember member,string admission="",string previous=""){Wire.Id(group);ValidateMember(member);var c=new GroupCertificate(group,issuer.Member.Id,member,"",admission,previous);return c with{Signature=issuer.Sign(CertificateData(c))};}
    public static DeviceGroup Create(GroupIdentity identity,string name,GroupConnection connection){connection.Validate();if(string.IsNullOrWhiteSpace(name)||name.Length>64||name.Any(char.IsControl))throw new ArgumentException("同步组名称无效");string id=Guid.NewGuid().ToString("N");var root=Certify(id,identity,identity.Member);return new(){Id=id,Name=name,StorageVersion=2,Connection=connection,Access=Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),DirectoryKey=Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),Root=root,Members=new(){{identity.Member.Id,root}}};}
    public static byte[] FeedData(GroupFeed f)=>Bytes($"clipbridge:group-feed:1\n{f.Group}\n{f.Author}\n{f.Sequence}\n"+string.Join("\n",f.Certificates.Select(CertificateHash).Order(StringComparer.Ordinal))+"\n"+string.Join(",",f.Removed.Order(StringComparer.Ordinal)));
    public static GroupFeed Feed(DeviceGroup group,GroupIdentity identity){var f=new GroupFeed(group.Id,identity.Member.Id,group.Sequence,group.OwnCertificates.ToList(),group.OwnRemoved.ToList(),"");return f with{Signature=identity.Sign(FeedData(f))};}
    public static string Revocation(GroupCertificate c)=>c.Member.Id+(c.Admission.Length==0?"":":"+c.Admission);
    public static void RefreshRemoved(DeviceGroup group){foreach(var f in group.Feeds.Values)group.Revoked.UnionWith(f.Removed);if(group.Revoked.Count==0)group.Revoked.UnionWith(group.Removed);group.Removed=group.Members.Values.Where(c=>group.Revoked.Contains(Revocation(c))).Select(c=>c.Member.Id).ToHashSet();}
    private static void ValidateCertificateEpoch(GroupCertificate c){if(c.Admission.Length==0){if(c.Previous.Length!=0)throw new CryptographicException();return;}Wire.Id(c.Admission);if(c.Issuer==c.Member.Id)throw new CryptographicException();if(c.Previous.Length!=64||c.Previous.Any(ch=>!"0123456789abcdef".Contains(ch)))throw new CryptographicException();}
    public static bool Accept(DeviceGroup group,GroupFeed feed){
        RefreshRemoved(group);Wire.Id(feed.Author);if(feed.Group!=group.Id||feed.Sequence<1||feed.Certificates.Count>128||feed.Removed.Count>128)throw new CryptographicException("成员清单无效");
        if(group.Removed.Contains(feed.Author)||!group.Members.TryGetValue(feed.Author,out var author))return false;
        if(!Verify(author.Member,FeedData(feed),feed.Signature))throw new CryptographicException("成员清单签名无效");bool newer=true;
        if(group.Feeds.TryGetValue(feed.Author,out var previous)){if(feed.Sequence<previous.Sequence)return false;if(feed.Sequence==previous.Sequence){if(!CryptographicOperations.FixedTimeEquals(SHA256.HashData(FeedData(feed)),SHA256.HashData(FeedData(previous))))throw new CryptographicException("成员清单发生分叉");newer=false;}}
        foreach(var cert in feed.Certificates){ValidateMember(cert.Member);ValidateCertificateEpoch(cert);if(cert.Group!=group.Id||cert.Issuer!=feed.Author||!Verify(author.Member,CertificateData(cert),cert.Signature))throw new CryptographicException("入组授权无效");if(group.Members.TryGetValue(cert.Member.Id,out var current)&&current.Member!=cert.Member)throw new CryptographicException("设备身份发生变化");}
        foreach(string token in feed.Removed){var parts=token.Split(':');if(parts.Length>2)throw new CryptographicException();foreach(var part in parts)Wire.Id(part);}
        var members=new Dictionary<string,GroupCertificate>(group.Members);bool changed=false;foreach(var cert in feed.Certificates){if(!members.TryGetValue(cert.Member.Id,out var current)){members.Add(cert.Member.Id,cert);changed=true;}else if(cert.Admission.Length>0&&cert.Previous==CertificateHash(current)){members[cert.Member.Id]=cert;changed=true;}}
        var revoked=group.Revoked.Union(feed.Removed).ToHashSet();if(members.Values.Count(c=>!revoked.Contains(Revocation(c)))>MaxMembers||members.Count>MaxHistory)throw new InvalidOperationException("一个同步组最多支持 8 台设备");
        group.Members=members;group.Revoked=revoked;group.Feeds[feed.Author]=feed;RefreshRemoved(group);return newer||changed;
    }
    public static void Add(DeviceGroup group,GroupIdentity identity,GroupMember member){RefreshRemoved(group);if(group.Removed.Contains(identity.Member.Id))throw new InvalidOperationException("邀请设备已被移出同步组");bool rejoining=group.Removed.Contains(member.Id);if(group.Members.ContainsKey(member.Id)&&!rejoining)throw new InvalidOperationException("此设备已在同步组内");if(rejoining&&group.Members[member.Id].Member!=member)throw new CryptographicException("设备身份与原记录不一致");if(group.Members.Keys.Except(group.Removed).Count()>=MaxMembers||!rejoining&&group.Members.Count>=MaxHistory||group.OwnCertificates.Count>=128)throw new InvalidOperationException("同步组人数或历史授权达到上限");var cert=Certify(group.Id,identity,member,rejoining?Guid.NewGuid().ToString("N"):"",rejoining?CertificateHash(group.Members[member.Id]):"");group.OwnCertificates.Add(cert);group.Sequence++;Accept(group,Feed(group,identity));}
    public static void Remove(DeviceGroup group,GroupIdentity identity,string member){RefreshRemoved(group);Wire.Id(member);if(group.Removed.Contains(identity.Member.Id)||!group.Members.ContainsKey(member))throw new InvalidOperationException("不能移除此设备");string token=Revocation(group.Members[member]);if(!group.OwnRemoved.Contains(token))group.OwnRemoved.Add(token);group.Sequence++;Accept(group,Feed(group,identity));}
    public static DeviceGroup Install(DeviceGroup? old,DeviceGroup received,GroupIdentity identity){Validate(received);string own=identity.Member.Id;if(!received.Members.TryGetValue(own,out var certificate)||certificate.Member!=identity.Member||received.Removed.Contains(own))throw new CryptographicException("本机入组授权无效");if(old is not null){if(CertificateHash(old.Root)!=CertificateHash(received.Root)||old.DirectoryKey!=received.DirectoryKey)throw new CryptographicException("同步组身份不一致");RefreshRemoved(old);if(!old.Removed.Contains(own)&&(!received.Revoked.Contains(Revocation(old.Members[own]))||CertificateHash(certificate)==CertificateHash(old.Members[own])))throw new InvalidOperationException("本机已加入此同步组");if(certificate.Admission.Length==0||CertificateHash(certificate)==CertificateHash(old.Members[own]))throw new CryptographicException("重新加入需要新的设备授权");received.Revoked.UnionWith(old.Revoked);}
        var prior=received.Feeds.GetValueOrDefault(own);received.OwnCertificates=(old?.OwnCertificates??[]).Concat(prior?.Certificates??[]).Where(c=>received.Members.TryGetValue(c.Member.Id,out var known)&&known.Member==c.Member).DistinctBy(CertificateHash).ToList();received.OwnRemoved=(old?.OwnRemoved??[]).Concat(prior?.Removed??[]).Distinct().ToList();received.Sequence=Math.Max(old?.Sequence??0,prior?.Sequence??0)+1;received.Enabled=true;received.PublishedSequence=0;received.PublishRetryAfter=0;received.CheckedAt=0;received.SuppressedPeers=[];received.Feeds[own]=Feed(received,identity);RefreshRemoved(received);if(received.Removed.Contains(own)||received.Removed.Contains(certificate.Issuer))throw new CryptographicException("当前邀请授权已被撤销");return received;
    }
    public static void Validate(DeviceGroup group){
        Wire.Id(group.Id);RefreshRemoved(group);group.Connection.Validate();if(Convert.FromBase64String(group.DirectoryKey).Length!=32)throw new CryptographicException();if(group.Root.Group!=group.Id||group.Root.Issuer!=group.Root.Member.Id||!Verify(group.Root.Member,CertificateData(group.Root),group.Root.Signature))throw new CryptographicException("同步组信任锚无效");
        if(group.Members.Count is <1 or >MaxHistory||group.Name.Length is <1 or >64)throw new CryptographicException();var valid=new Dictionary<string,GroupCertificate>{{group.Root.Member.Id,group.Root}};
        for(int pass=0;pass<MaxHistory;pass++)foreach(var c in group.Members.Values){ValidateMember(c.Member);ValidateCertificateEpoch(c);if(c.Group!=group.Id)throw new CryptographicException();if(valid.TryGetValue(c.Issuer,out var issuer)&&Verify(issuer.Member,CertificateData(c),c.Signature))valid.TryAdd(c.Member.Id,c);}
        if(valid.Count!=group.Members.Count||!group.Members.TryGetValue(group.Root.Member.Id,out var root)||root.Member!=group.Root.Member)throw new CryptographicException("同步组授权链不完整");if(group.Connection.Transport=="relay"&&Convert.FromBase64String(group.Access).Length!=32)throw new CryptographicException();
        foreach(var c in group.Members.Values)if(!valid.TryGetValue(c.Issuer,out var signer)||!Verify(signer.Member,CertificateData(c),c.Signature))throw new CryptographicException("成员证书无效");
        foreach(var f in group.Feeds.Values)if(f.Group!=group.Id||!valid.TryGetValue(f.Author,out var author)||!Verify(author.Member,FeedData(f),f.Signature))throw new CryptographicException("同步组授权状态无效");
    }
    public static ClientConfig Channel(DeviceGroup group,GroupIdentity self,GroupMember peer){
        ValidateMember(peer);string first=string.CompareOrdinal(self.Member.Id,peer.Id)<0?self.Member.Id:peer.Id,last=first==self.Member.Id?peer.Id:self.Member.Id;
        string pair=Hash(Bytes($"clipbridge:mesh-pair:1:{group.Id}:{first}:{last}"))[..32];
        using var own=ECDiffieHellman.Create();own.ImportPkcs8PrivateKey(Convert.FromBase64String(self.AgreementPrivate),out _);using var other=ECDiffieHellman.Create();other.ImportSubjectPublicKeyInfo(Convert.FromBase64String(peer.AgreementKey),out _);byte[] shared=own.DeriveRawSecretAgreement(other.PublicKey);
        try{byte[] key=HKDF.DeriveKey(HashAlgorithmName.SHA256,shared,32,SHA256.HashData(Bytes("clipbridge:group-channel:1:"+group.Id)),Bytes(pair));string device=Hash(Bytes(pair+":"+self.Member.Id))[..32],to=Hash(Bytes(pair+":"+peer.Id))[..32];
            string server=group.Connection.Transport=="relay"?group.Connection.Server.TrimEnd('/')+"/v2/groups/"+group.Id+"/store/":group.Connection.Server.TrimEnd('/')+"/"+StoragePrefix(group);
            var c=new ClientConfig(server,pair,device,to,Convert.ToBase64String(SHA256.HashData(Bytes(pair+device))),Convert.ToBase64String(key)){Transport="webdav",Username=group.Connection.Transport=="relay"?"group":group.Connection.Username,Password=group.Connection.Transport=="relay"?group.Access:group.Connection.Password};c.Validate();return c;
        }finally{CryptographicOperations.ZeroMemory(shared);}
    }
    public static string StoragePrefix(DeviceGroup group)=>(group.StorageVersion>=2?"clipbridge/groups/":"clipbridge-groups/")+group.Id+"/";
    public static DeviceGroup Export(DeviceGroup group){var clone=JsonSerializer.Deserialize<DeviceGroup>(JsonSerializer.Serialize(group,Wire.Json),Wire.Json)!;clone.OwnCertificates=[];clone.OwnRemoved=[];clone.Sequence=1;clone.CheckedAt=0;clone.PublishedSequence=0;clone.SuppressedPeers=[];return clone;}
}
