package io.clipbridge.android;

import org.json.*;
import java.nio.charset.StandardCharsets;
import java.util.*;
import javax.crypto.Cipher;

public final class AttachmentSyncTest {
    static int count;
    static void check(boolean ok,String name){if(!ok)throw new AssertionError(name);count++;System.out.println("PASS "+name);}
    static JSONObject empty()throws Exception{return new JSONObject().put("offers",new JSONArray()).put("decisions",new JSONArray());}
    static JSONObject job(String pair,String phase,long expires)throws Exception{return new JSONObject().put("pairs",new JSONArray().put(pair)).put("phase",phase).put("offer",new JSONObject().put("expires",expires));}
    static String encrypted(JSONObject c,JSONObject index)throws Exception{
        byte[] nonce=new byte[12];new java.security.SecureRandom().nextBytes(nonce);
        JSONObject e=new JSONObject().put("version",1).put("pair",c.getString("pair")).put("from",c.getString("device")).put("to",c.getString("peer")).put("id","d".repeat(32)).put("clock",System.currentTimeMillis()).put("nonce",Protocol.encode(nonce));
        return e.put("data",Protocol.encode(Crypto.transform(Cipher.ENCRYPT_MODE,Protocol.decode(MediaProtocol.indexConfig(c).getString("key")),nonce,Protocol.header(e),index.toString().getBytes(StandardCharsets.UTF_8)))).toString();
    }
    public static void main(String[] args)throws Exception{
        String pair="a".repeat(32);long now=System.currentTimeMillis()/1000;
        JSONObject state=new JSONObject().put("outgoing",new JSONArray()).put("incoming",new JSONArray());
        check(!AttachmentSync.shouldPublish(empty(),false),"idle service never uploads an empty manifest");
        check(AttachmentSync.shouldPublish(empty(),true),"previously advertised manifest is cleared once");
        check(AttachmentSync.shouldPublish(empty().put("offers",new JSONArray().put(new JSONObject())),false),"outgoing offer is published");
        check(AttachmentSync.shouldPublish(empty().put("decisions",new JSONArray().put(new JSONObject())),false),"receive decision is published");
        check(!AttachmentSync.pending(state,pair,now),"idle connection does not alert");
        for(String phase:new String[]{"waiting","sending"}){state.put("outgoing",new JSONArray().put(job(pair,phase,now+60)));check(AttachmentSync.pending(state,pair,now),"live outgoing "+phase+" can alert");}
        for(String phase:new String[]{"done","rejected","cancelled"}){state.put("outgoing",new JSONArray().put(job(pair,phase,now+60)));check(!AttachmentSync.pending(state,pair,now),"finished outgoing "+phase+" does not alert");}
        state.put("outgoing",new JSONArray().put(job(pair,"waiting",now)));check(!AttachmentSync.pending(state,pair,now),"expired job does not alert");
        state.put("outgoing",new JSONArray().put(job("b".repeat(32),"waiting",now+60)));check(!AttachmentSync.pending(state,pair,now),"unrelated route does not alert");
        state.put("outgoing",new JSONArray());
        for(String phase:new String[]{"waiting","accepted","preview","done","rejected"}){state.put("incoming",new JSONArray().put(job(pair,phase,now+60)));check(AttachmentSync.pending(state,pair,now)==Set.of("waiting","accepted").contains(phase),"incoming "+phase+" alert policy");}
        JSONObject c=new JSONObject().put("pair",pair).put("device","b".repeat(32)).put("peer","c".repeat(32)).put("server","https://example.com").put("token",Protocol.encode(new byte[32])).put("key",Protocol.encode(new byte[32]));
        JSONObject receiver=new JSONObject(c.toString()).put("device",c.getString("peer")).put("peer",c.getString("device"));
        JSONObject fresh=empty().put("created",now).put("expires",now+3600);
        check(MediaProtocol.openIndex(receiver,encrypted(c,fresh))!=null,"fresh authenticated index accepted");
        JSONObject expired=empty().put("created",now-3600).put("expires",now);
        check(MediaProtocol.openIndex(receiver,encrypted(c,expired))==null,"authenticated expired index is absent, not a connection error");
        try{MediaProtocol.openIndex(receiver,encrypted(c,empty().put("created",now+3600).put("expires",now+7200)));throw new AssertionError("future index");}catch(SecurityException expected){check(true,"future index still rejected");}
        try{MediaProtocol.openIndex(receiver,encrypted(c,empty().put("created",now-7200).put("expires",now)));throw new AssertionError("invalid lifetime");}catch(SecurityException expected){check(true,"expired index with invalid lifetime still rejected");}
        JSONObject tampered=new JSONObject(encrypted(c,expired));byte[] cipher=Protocol.decode(tampered.getString("data"));cipher[0]^=1;tampered.put("data",Protocol.encode(cipher));
        try{MediaProtocol.openIndex(receiver,tampered.toString());throw new AssertionError("tampered expired index");}catch(javax.crypto.AEADBadTagException expected){check(true,"expired index still requires valid authentication");}
        MediaWire wire=new MediaWire(c);check(wire.matches(new JSONObject(c.toString()).put("transport","relay")),"implicit relay matches explicit relay");
        JSONObject changed=new JSONObject(c.toString()).put("transport","webdav").put("username","test").put("password","test");
        check(!wire.matches(changed),"transport switch invalidates cached client");
        c.put("server","https://other.example.com");check(!wire.matches(c),"client keeps immutable config snapshot");
        check(!wire.matches(new JSONObject(receiver.toString()).put("token",Protocol.encode(new byte[33]))),"credential change invalidates client");
        JSONObject profile=new JSONObject().put("config",receiver).put("name","Phone\nOne");
        check(AttachmentSync.label(profile).equals("Phone One · 专用中转 "),"relay alert identifies connection without WebDAV wording");
        profile.put("config",changed);check(AttachmentSync.label(profile).contains("WebDAV"),"only actual WebDAV config gets WebDAV label");
        profile.put("connectionTransport","relay");check(!AttachmentSync.isWebDav(profile)&&!AttachmentSync.label(profile).contains("WebDAV"),"relay group using a WebDAV-compatible wire is labelled relay");
        profile.put("connectionTransport","webdav");check(AttachmentSync.isWebDav(profile),"real WebDAV group uses WebDAV retry policy");
        System.out.println("PASS "+count+" attachment notification regression checks");
    }
}
