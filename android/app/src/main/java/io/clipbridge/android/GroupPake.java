package io.clipbridge.android;
import org.bouncycastle.crypto.agreement.jpake.*;
import java.math.BigInteger;
import org.json.*;
import java.security.*;
import java.util.*;
import javax.crypto.*;
import javax.crypto.spec.*;
final class GroupPake {
    private static final String ALPHABET="23456789ABCDEFGHJKLMNPQRSTUVWXYZ";
    private final JPAKEParticipant participant;private final String locator,role,own;private String peer="";private BigInteger material;private byte[] master;
    static String newCode(){byte[] bytes=new byte[16];new SecureRandom().nextBytes(bytes);StringBuilder s=new StringBuilder();for(byte b:bytes)s.append(ALPHABET.charAt((b&255)%ALPHABET.length()));return s.toString();}
    static String normalize(String text){String code=java.text.Normalizer.normalize(text,java.text.Normalizer.Form.NFKC).toUpperCase(java.util.Locale.ROOT).replaceAll("[\\s\\p{Z}\\p{Pd}\\p{Cf}]","");if(code.length()!=16||code.chars().anyMatch(c->ALPHABET.indexOf(c)<0))throw new IllegalArgumentException("配对码应为 16 位字母或数字");return code;}
    static String display(String code){return code.substring(0,4)+"-"+code.substring(4,8)+"-"+code.substring(8,12)+"-"+code.substring(12);}
    final String commitNonce=GroupCrypto.secret();
    GroupPake(String code,boolean host){this(code,host,"");}
    GroupPake(String code,boolean host,String commitment){code=normalize(code);locator=code.substring(0,6);role=host?"a":"b";try{own=role+":"+locator+":"+GroupCrypto.hash(GroupCrypto.bytes("clipbridge:group-commit:1:"+locator+":"+role+":"+commitNonce+":"+commitment)).substring(0,32);}catch(Exception e){throw new IllegalStateException(e);}participant=new JPAKEParticipant(own,code.substring(6).toCharArray());}
    private static BigInteger num(String value){if(value.length()<1||value.length()>800||!value.matches("-?[0-9a-f]+"))throw new SecurityException();return new BigInteger(value,16);}
    private void check(JSONObject message,int round,int count)throws Exception{String id=message.getString("id");if(message.getInt("round")!=round||message.getJSONArray("values").length()!=count||id.length()>100||!id.startsWith((role.equals("a")?"b":"a")+":"+locator+":")||!peer.isEmpty()&&!id.equals(peer))throw new SecurityException("配对握手无效");Protocol.id(id.split(":")[2]);peer=id;}
    private JSONObject message(int round,BigInteger...values)throws Exception{JSONArray list=new JSONArray();for(BigInteger n:values)list.put(n.toString(16));return new JSONObject().put("round",round).put("id",own).put("values",list);}
    JSONObject round1()throws Exception{JPAKERound1Payload m=participant.createRound1PayloadToSend();return message(1,m.getGx1(),m.getGx2(),m.getKnowledgeProofForX1()[0],m.getKnowledgeProofForX1()[1],m.getKnowledgeProofForX2()[0],m.getKnowledgeProofForX2()[1]);}
    JSONObject round2(JSONObject other)throws Exception{check(other,1,6);JSONArray v=other.getJSONArray("values");participant.validateRound1PayloadReceived(new JPAKERound1Payload(peer,num(v.getString(0)),num(v.getString(1)),new BigInteger[]{num(v.getString(2)),num(v.getString(3))},new BigInteger[]{num(v.getString(4)),num(v.getString(5))}));JPAKERound2Payload m=participant.createRound2PayloadToSend();return message(2,m.getA(),m.getKnowledgeProofForX2s()[0],m.getKnowledgeProofForX2s()[1]);}
    JSONObject round3(JSONObject other)throws Exception{check(other,2,3);JSONArray v=other.getJSONArray("values");participant.validateRound2PayloadReceived(new JPAKERound2Payload(peer,num(v.getString(0)),new BigInteger[]{num(v.getString(1)),num(v.getString(2))}));material=participant.calculateKeyingMaterial();return message(3,participant.createRound3PayloadToSend(material).getMacTag());}
    void finish(JSONObject other)throws Exception{check(other,3,1);if(material==null)throw new SecurityException();participant.validateRound3PayloadReceived(new JPAKERound3Payload(peer,num(other.getJSONArray("values").getString(0))),material);String a=role.equals("a")?own:peer,b=role.equals("b")?own:peer;master=GroupCrypto.hkdf(org.bouncycastle.util.BigIntegers.asUnsignedByteArray(material),MessageDigest.getInstance("SHA-256").digest(GroupCrypto.bytes("clipbridge:group-pake:1:"+locator)),GroupCrypto.bytes(a+"\n"+b));material=null;}
    boolean verifyCommitment(String commitment,String nonce)throws Exception{if(GroupCrypto.un64(nonce).length!=32)return false;String expected=GroupCrypto.hash(GroupCrypto.bytes("clipbridge:group-commit:1:"+locator+":"+(role.equals("a")?"b":"a")+":"+nonce+":"+commitment)).substring(0,32);return peer.endsWith(":"+expected);}
    String binding()throws Exception{if(master==null)throw new SecurityException();return GroupCrypto.b64(MessageDigest.getInstance("SHA-256").digest(master));}
    JSONObject seal(String slot,String plain)throws Exception{if(master==null||plain.length()>150000)throw new SecurityException();return GroupCrypto.seal(master,"clipbridge:group-session:1:"+locator+":"+slot,plain);}
    String open(String slot,JSONObject box)throws Exception{if(master==null)throw new SecurityException();return GroupCrypto.open(master,"clipbridge:group-session:1:"+locator+":"+slot,box);}
    String sas(String a,String b)throws Exception{if(master==null)throw new SecurityException();Mac h=Mac.getInstance("HmacSHA256");h.init(new SecretKeySpec(master,"HmacSHA256"));byte[] digest=h.doFinal(GroupCrypto.bytes("clipbridge:group-sas:1"));long v=((digest[0]&255L)<<24)|((digest[1]&255L)<<16)|((digest[2]&255L)<<8)|(digest[3]&255L);return String.format(java.util.Locale.ROOT,"%06d",v%1000000);}
    String proof(JSONObject self,JSONObject other,String a,String b,String who)throws Exception{if(master==null)throw new SecurityException();byte[] raw=GroupCrypto.agreement(self,other),key=GroupCrypto.hkdf(raw,master,GroupCrypto.bytes("clipbridge:group-possession:1:"+locator));Arrays.fill(raw,(byte)0);Mac h=Mac.getInstance("HmacSHA256");h.init(new SecretKeySpec(key,"HmacSHA256"));return GroupCrypto.b64(h.doFinal(GroupCrypto.bytes(who+"\n"+a+"\n"+b)));}
    void close(){if(master!=null)Arrays.fill(master,(byte)0);master=null;material=null;}
}
