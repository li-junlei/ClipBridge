package io.clipbridge.android;

import android.net.Uri;
import org.json.JSONObject;
import javax.crypto.Cipher;
import javax.crypto.spec.GCMParameterSpec;
import javax.crypto.spec.SecretKeySpec;
import javax.net.ssl.HttpsURLConnection;
import java.net.URI;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.util.Arrays;
import java.util.Base64;

public final class PairingClient {
    public static String server(String qr) throws Exception {
        if(qr==null || qr.length()>2048) throw new IllegalArgumentException("无效的配对二维码");
        Uri u=Uri.parse(qr);
        if(!"clipbridge".equals(u.getScheme()) || !"pair".equals(u.getHost())) throw new IllegalArgumentException("请扫描 ClipBridge 的配对二维码");
        String server=u.getQueryParameter("server");URI origin=new URI(server);
        if(!"https".equals(origin.getScheme()) || origin.getHost()==null || origin.getUserInfo()!=null || origin.getQuery()!=null || origin.getFragment()!=null || !(origin.getPath().isEmpty() || origin.getPath().equals("/"))) throw new IllegalArgumentException("配对必须使用 HTTPS 服务器");
        Protocol.id(u.getQueryParameter("id"));
        if(Base64.getUrlDecoder().decode(u.getFragment()).length!=32) throw new IllegalArgumentException("二维码密钥无效");
        return server.replaceAll("/+$", "");
    }
    public static String claim(String qr) throws Exception {
        String server=server(qr);Uri u=Uri.parse(qr);String ticket=u.getQueryParameter("id");
        byte[] key=Base64.getUrlDecoder().decode(u.getFragment());
        HttpsURLConnection connection=(HttpsURLConnection)new URL(server+"/v1/pairing-invites/claim").openConnection();
        connection.setInstanceFollowRedirects(false);connection.setConnectTimeout(10000);connection.setReadTimeout(15000);
        connection.setRequestMethod("POST");connection.setDoOutput(true);connection.setRequestProperty("Content-Type","application/json");
        try {
            byte[] body=new JSONObject().put("ticket",ticket).toString().getBytes(StandardCharsets.UTF_8);
            connection.setFixedLengthStreamingMode(body.length);try(java.io.OutputStream out=connection.getOutputStream()){out.write(body);}
            int status=connection.getResponseCode();
            if(status==410) throw new IllegalArgumentException("二维码已使用或已过期，请在电脑刷新二维码");
            if(status!=200) throw new java.io.IOException("服务器返回 HTTP "+status);
            java.io.ByteArrayOutputStream out=new java.io.ByteArrayOutputStream();
            try(java.io.InputStream in=connection.getInputStream()) { byte[] buffer=new byte[2048];int n;while((n=in.read(buffer))!=-1){out.write(buffer,0,n);if(out.size()>20000)throw new IllegalArgumentException("配对数据过大");} }
            JSONObject invite=new JSONObject(out.toString(StandardCharsets.UTF_8.name()));
            long expires=invite.getLong("expires");
            if(!ticket.equals(invite.getString("id")) || expires<=System.currentTimeMillis()/1000) throw new IllegalArgumentException("配对邀请已过期");
            byte[] nonce=Protocol.decode(invite.getString("nonce")),data=Protocol.decode(invite.getString("data"));
            if(nonce.length!=12 || data.length<16 || data.length>8192) throw new IllegalArgumentException("无效的加密邀请");
            Cipher cipher=Cipher.getInstance("AES/GCM/NoPadding");cipher.init(Cipher.DECRYPT_MODE,new SecretKeySpec(key,"AES"),new GCMParameterSpec(128,nonce));
            cipher.updateAAD(("clipbridge-pair:1:"+ticket+":"+expires).getBytes(StandardCharsets.US_ASCII));
            byte[] plain=cipher.doFinal(data);
            try {
                JSONObject config=new JSONObject(new String(plain,StandardCharsets.UTF_8));Protocol.config(config);
                if(!server.equals(config.getString("server").replaceAll("/+$", ""))) throw new IllegalArgumentException("邀请服务器不匹配");
                return config.toString();
            } finally { Arrays.fill(plain,(byte)0); }
        } finally { Arrays.fill(key,(byte)0);connection.disconnect(); }
    }
}
