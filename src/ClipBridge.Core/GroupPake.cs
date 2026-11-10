using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Org.BouncyCastle.Crypto.Agreement.JPake;
using Big=Org.BouncyCastle.Math.BigInteger;
namespace ClipBridge;
public record PakeMessage(int Round,string Id,string[] Values);
public record GroupBox(string Nonce,string Data);
public sealed class GroupPake:IDisposable
{
    private const string Alphabet="23456789ABCDEFGHJKLMNPQRSTUVWXYZ";
    private readonly JPakeParticipant participant;
    private readonly string locator,role,own;
    private string peer="";
    private Big? material;
    private byte[]? master;
    public static string NewCode(){var b=RandomNumberGenerator.GetBytes(16);return new string(b.Select(v=>Alphabet[v%Alphabet.Length]).ToArray());}
    public static string Normalize(string code){string s=new(code.Normalize(NormalizationForm.FormKC).ToUpperInvariant().Where(c=>!char.IsWhiteSpace(c)&&char.GetUnicodeCategory(c)!=System.Globalization.UnicodeCategory.DashPunctuation&&char.GetUnicodeCategory(c)!=System.Globalization.UnicodeCategory.Format).ToArray());if(s.Length!=16||s.Any(c=>!Alphabet.Contains(c)))throw new ArgumentException("配对码应为 16 位字母或数字");return s;}
    public static string Display(string code)=>string.Join("-",Enumerable.Range(0,4).Select(i=>code.Substring(i*4,4)));
    public string CommitNonce{get;}=Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    public GroupPake(string code,bool inviter,string commitment=""){code=Normalize(code);locator=code[..6];role=inviter?"a":"b";own=role+":"+locator+":"+Groups.Hash(Groups.Bytes("clipbridge:group-commit:1:"+locator+":"+role+":"+CommitNonce+":"+commitment))[..32];participant=new(own,code[6..].ToCharArray());}
    private static string Hex(Big value)=>value.ToString(16);
    private static Big Num(string value){if(value.Length is <1 or >800||value.Any(c=>!"-0123456789abcdef".Contains(c))||value.Count(c=>c=='-')>1||value.LastIndexOf('-')>0)throw new CryptographicException();return new(value,16);}
    private void Check(PakeMessage m,int round,int count){if(m.Round!=round||m.Values.Length!=count||m.Id.Length>100||!m.Id.StartsWith((role=="a"?"b":"a")+":"+locator+":",StringComparison.Ordinal)||peer.Length!=0&&m.Id!=peer)throw new CryptographicException("配对握手无效");Wire.Id(m.Id.Split(':')[2]);peer=m.Id;}
    public PakeMessage Round1(){var m=participant.CreateRound1PayloadToSend();return new(1,m.ParticipantId,[Hex(m.Gx1),Hex(m.Gx2),..m.KnowledgeProofForX1.Select(Hex),..m.KnowledgeProofForX2.Select(Hex)]);}
    public PakeMessage Round2(PakeMessage other){Check(other,1,6);var v=other.Values.Select(Num).ToArray();participant.ValidateRound1PayloadReceived(new(other.Id,v[0],v[1],[v[2],v[3]],[v[4],v[5]]));var m=participant.CreateRound2PayloadToSend();return new(2,m.ParticipantId,[Hex(m.A),..m.KnowledgeProofForX2s.Select(Hex)]);}
    public PakeMessage Round3(PakeMessage other){Check(other,2,3);var v=other.Values.Select(Num).ToArray();participant.ValidateRound2PayloadReceived(new(other.Id,v[0],[v[1],v[2]]));material=participant.CalculateKeyingMaterial();var m=participant.CreateRound3PayloadToSend(material);return new(3,m.ParticipantId,[Hex(m.MacTag)]);}
    public void Finish(PakeMessage other){Check(other,3,1);if(material is null)throw new CryptographicException();participant.ValidateRound3PayloadReceived(new(other.Id,Num(other.Values[0])),material);string a=role=="a"?own:peer,b=role=="b"?own:peer;master=HKDF.DeriveKey(HashAlgorithmName.SHA256,material.ToByteArrayUnsigned(),32,SHA256.HashData(Groups.Bytes("clipbridge:group-pake:1:"+locator)),Groups.Bytes(a+"\n"+b));material=null;}
    public bool VerifyCommitment(string commitment,string nonce){if(Convert.FromBase64String(nonce).Length!=32)return false;string expected=Groups.Hash(Groups.Bytes("clipbridge:group-commit:1:"+locator+":"+(role=="a"?"b":"a")+":"+nonce+":"+commitment))[..32];return peer.EndsWith(":"+expected,StringComparison.Ordinal);}
    public string Binding=>Convert.ToBase64String(SHA256.HashData(master??throw new CryptographicException()));
    public GroupBox Seal(string slot,string plain){if(master is null||plain.Length>150000)throw new CryptographicException();byte[] nonce=RandomNumberGenerator.GetBytes(12),p=Groups.Bytes(plain),c=new byte[p.Length+16];using var aes=new AesGcm(master,16);aes.Encrypt(nonce,p,c.AsSpan(0,p.Length),c.AsSpan(p.Length),Groups.Bytes("clipbridge:group-session:1:"+locator+":"+slot));return new(Convert.ToBase64String(nonce),Convert.ToBase64String(c));}
    public string Open(string slot,GroupBox box){if(master is null)throw new CryptographicException();byte[] n=Convert.FromBase64String(box.Nonce),c=Convert.FromBase64String(box.Data);if(n.Length!=12||c.Length is <16 or >200000)throw new CryptographicException();byte[] p=new byte[c.Length-16];using var aes=new AesGcm(master,16);aes.Decrypt(n,c.AsSpan(0,p.Length),c.AsSpan(p.Length),p,Groups.Bytes("clipbridge:group-session:1:"+locator+":"+slot));return Encoding.UTF8.GetString(p);}
    public string Sas(string a,string b){if(master is null)throw new CryptographicException();byte[] h=HMACSHA256.HashData(master,Groups.Bytes("clipbridge:group-sas:1"));uint v=System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(h);return(v%1000000).ToString("D6");}
    public string IdentityProof(GroupIdentity self,GroupMember peerMember,string a,string b,string who){if(master is null)throw new CryptographicException();using var own=ECDiffieHellman.Create();own.ImportPkcs8PrivateKey(Convert.FromBase64String(self.AgreementPrivate),out _);using var other=ECDiffieHellman.Create();other.ImportSubjectPublicKeyInfo(Convert.FromBase64String(peerMember.AgreementKey),out _);byte[] raw=own.DeriveRawSecretAgreement(other.PublicKey);try{byte[] key=HKDF.DeriveKey(HashAlgorithmName.SHA256,raw,32,master,Groups.Bytes("clipbridge:group-possession:1:"+locator));return Convert.ToBase64String(HMACSHA256.HashData(key,Groups.Bytes(who+"\n"+a+"\n"+b)));}finally{CryptographicOperations.ZeroMemory(raw);}}
    public void Dispose(){if(master is not null)CryptographicOperations.ZeroMemory(master);master=null;material=null;}
}
