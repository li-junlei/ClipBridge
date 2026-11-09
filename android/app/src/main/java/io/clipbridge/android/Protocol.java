package io.clipbridge.android;

import org.json.JSONObject;
import javax.crypto.Cipher;
import java.net.URI;
import java.nio.charset.StandardCharsets;
import java.security.SecureRandom;
import java.util.Base64;
import java.util.UUID;

final class Protocol {
    static byte[] decode(String s) { return Base64.getDecoder().decode(s); }
    static String encode(byte[] b) { return Base64.getEncoder().encodeToString(b); }
    static void id(String s) { if (!s.matches("[0-9a-f]{32}")) throw new IllegalArgumentException("Invalid ID"); }
    static void config(JSONObject c) throws Exception {
        for (String k : new String[]{"pair","device","peer"}) id(c.getString(k));
        URI uri = new URI(c.getString("server"));
        if (!"https".equals(uri.getScheme()) || uri.getHost() == null || uri.getUserInfo()!=null || uri.getQuery()!=null || uri.getFragment()!=null || !(uri.getPath().isEmpty() || uri.getPath().equals("/"))) throw new IllegalArgumentException("HTTPS origin required");
        if (c.getString("device").equals(c.getString("peer")) || decode(c.getString("key")).length!=32 || decode(c.getString("token")).length!=32) throw new IllegalArgumentException("Invalid configuration");
    }
    static void state(JSONObject c,JSONObject s) throws Exception {
        long clock=s.getLong("clock");String winner=s.getString("winner");
        if(clock<0 || clock>9007199254740991L || (clock==0 ? !winner.isEmpty() : !winner.equals(c.getString("device")) && !winner.equals(c.getString("peer")))) throw new SecurityException("Invalid replay state");
        if(s.has("pending")) {
            JSONObject e=s.getJSONObject("pending");header(e);
            if(!e.getString("pair").equals(c.getString("pair")) || !e.getString("from").equals(c.getString("device")) || !e.getString("to").equals(c.getString("peer")) || e.getLong("clock")!=clock || !winner.equals(c.getString("device"))) throw new SecurityException("Invalid pending state");
        }
    }
    static String header(JSONObject e) throws Exception {
        for (String k : new String[]{"pair","from","to","id"}) id(e.getString(k));
        long clock=e.getLong("clock");
        if(e.getInt("version")!=1 || clock<1 || clock>9007199254740991L || e.getString("from").equals(e.getString("to"))) throw new IllegalArgumentException("Invalid envelope");
        return "clipbridge:1:"+e.getString("pair")+":"+e.getString("from")+":"+e.getString("to")+":"+e.getString("id")+":"+clock;
    }
    static JSONObject seal(JSONObject c,long clock,String text) throws Exception {
        if(text.getBytes(StandardCharsets.UTF_8).length>65536) throw new IllegalArgumentException("Text too large");
        long now=System.currentTimeMillis()/1000;
        JSONObject p=new JSONObject().put("kind","text").put("text",text).put("created",now).put("expires",now+600);
        byte[] nonce=new byte[12]; new SecureRandom().nextBytes(nonce);
        JSONObject e=new JSONObject().put("version",1).put("pair",c.getString("pair")).put("from",c.getString("device")).put("to",c.getString("peer"))
            .put("id",UUID.randomUUID().toString().replace("-", "")).put("clock",clock).put("nonce",encode(nonce));
        e.put("data",encode(Crypto.transform(Cipher.ENCRYPT_MODE,decode(c.getString("key")),nonce,header(e),p.toString().getBytes(StandardCharsets.UTF_8))));
        return e;
    }
    static String open(JSONObject c,JSONObject e) throws Exception {
        String h=header(e);
        if(!e.getString("pair").equals(c.getString("pair")) || !e.getString("from").equals(c.getString("peer")) || !e.getString("to").equals(c.getString("device"))) throw new SecurityException("Wrong peer");
        byte[] nonce=decode(e.getString("nonce")), data=decode(e.getString("data"));
        if(nonce.length!=12 || data.length<16 || data.length>400000) throw new SecurityException("Invalid ciphertext");
        byte[] plain=Crypto.transform(Cipher.DECRYPT_MODE,decode(c.getString("key")),nonce,h,data);
        try {
            JSONObject p=new JSONObject(new String(plain,StandardCharsets.UTF_8));
            long now=System.currentTimeMillis()/1000, created=p.getLong("created"), expires=p.getLong("expires");
            String text=p.getString("text");
            if(!p.getString("kind").equals("text") || text.getBytes(StandardCharsets.UTF_8).length>65536 || created>now+60 || expires<=now || expires<created || expires-created>600) throw new SecurityException("Expired or invalid payload");
            return text;
        } finally { java.util.Arrays.fill(plain,(byte)0); }
    }
}
