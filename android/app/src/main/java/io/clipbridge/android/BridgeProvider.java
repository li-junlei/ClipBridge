package io.clipbridge.android;

import android.content.ContentProvider;
import android.content.ContentValues;
import android.content.SharedPreferences;
import android.database.Cursor;
import android.net.Uri;
import android.os.Binder;
import android.os.Bundle;
import android.security.keystore.KeyGenParameterSpec;
import android.security.keystore.KeyProperties;
import java.security.KeyStore;
import javax.crypto.Cipher;
import javax.crypto.KeyGenerator;
import javax.crypto.SecretKey;
import javax.crypto.spec.GCMParameterSpec;
import java.nio.charset.StandardCharsets;
import org.json.JSONObject;

public final class BridgeProvider extends ContentProvider {
    static final Uri URI=Uri.parse("content://io.clipbridge.android.bridge");
    private SharedPreferences prefs;
    public boolean onCreate(){ prefs=getContext().getSharedPreferences("bridge",0); return true; }
    private void authorize() {
        int caller=Binder.getCallingUid();
        if(caller==android.os.Process.myUid()) return;
        try {
            int uid=getContext().getPackageManager().getApplicationInfo("com.android.systemui",0).uid;
            if(caller==uid) return;
        } catch(Exception ignored){}
        throw new SecurityException("Caller not allowed");
    }
    private SecretKey key() throws Exception {
        KeyStore ks=KeyStore.getInstance("AndroidKeyStore"); ks.load(null);
        if(!ks.containsAlias("clipbridge.config")) {
            KeyGenerator g=KeyGenerator.getInstance("AES","AndroidKeyStore");
            g.init(new KeyGenParameterSpec.Builder("clipbridge.config",KeyProperties.PURPOSE_ENCRYPT|KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM).setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE).build()); g.generateKey();
        }
        return (SecretKey)ks.getKey("clipbridge.config",null);
    }
    private String wrap(String text) throws Exception {
        Cipher c=Cipher.getInstance("AES/GCM/NoPadding"); c.init(Cipher.ENCRYPT_MODE,key());
        return Protocol.encode(c.getIV())+":"+Protocol.encode(c.doFinal(text.getBytes(StandardCharsets.UTF_8)));
    }
    private String unwrap(String text) throws Exception {
        String[] parts=text.split(":"); Cipher c=Cipher.getInstance("AES/GCM/NoPadding");
        c.init(Cipher.DECRYPT_MODE,key(),new GCMParameterSpec(128,Protocol.decode(parts[0])));
        return new String(c.doFinal(Protocol.decode(parts[1])),StandardCharsets.UTF_8);
    }
    @Override public synchronized Bundle call(String method,String arg,Bundle extras) {
        authorize(); Bundle out=new Bundle();
        try {
            switch(method) {
                case "import":
                case "replace":
                    if(Binder.getCallingUid()!=android.os.Process.myUid()) throw new SecurityException();
                    JSONObject c=new JSONObject(arg); Protocol.config(c);
                    if(prefs.contains("config") && !method.equals("replace")) throw new IllegalStateException("Already paired");
                    String oldPair=prefs.contains("config")?new JSONObject(unwrap(prefs.getString("config",""))).getString("pair"):null;
                    SharedPreferences.Editor change=prefs.edit();
                    if(oldPair!=null) {JSONObject oldState=new JSONObject(unwrap(prefs.getString("state","")));oldState.remove("pending");change.putString("replay."+oldPair,wrap(oldState.toString()));}
                    String restored=oldPair!=null && oldPair.equals(c.getString("pair"))?prefs.getString("state",null):prefs.getString("replay."+c.getString("pair"),null);
                    String nextState=restored==null?"{\"clock\":0,\"winner\":\"\"}":unwrap(restored);
                    JSONObject restoredState=new JSONObject(nextState);restoredState.remove("pending");Protocol.state(c,restoredState);nextState=restoredState.toString();
                    if(!change.putString("config",wrap(c.toString())).putString("state",wrap(nextState)).remove("peerName").remove("lastSync").putBoolean("paused",false).commit()) throw new IllegalStateException("Storage failed");
                    break;
                case "delete":
                    if(Binder.getCallingUid()!=android.os.Process.myUid()) throw new SecurityException();
                    SharedPreferences.Editor deletion=prefs.edit();
                    if(prefs.contains("config")){JSONObject old=new JSONObject(unwrap(prefs.getString("config","")));JSONObject replay=new JSONObject(unwrap(prefs.getString("state","")));replay.remove("pending");deletion.putString("replay."+old.getString("pair"),wrap(replay.toString()));}
                    if(!deletion.remove("config").remove("state").remove("peerName").remove("lastSync").putBoolean("paused",true).putString("status","未配对").commit())throw new IllegalStateException();
                    break;
                case "metrics":
                    JSONObject current=new JSONObject(unwrap(prefs.getString("config","")));
                    if(extras==null || !current.getString("pair").equals(extras.getString("pair")) || !current.getString("device").equals(extras.getString("device")))throw new IllegalStateException();
                    SharedPreferences.Editor metrics=prefs.edit();
                    if(extras.containsKey("lastSync"))metrics.putLong("lastSync",extras.getLong("lastSync"));
                    if(extras.containsKey("peerName")){String name=extras.getString("peerName");if(name==null || name.length()>80 || name.matches(".*[\\p{Cntrl}].*"))throw new IllegalArgumentException();metrics.putString("peerName",name);}
                    if(!metrics.commit())throw new IllegalStateException();break;
                case "load":
                    if(prefs.contains("config")) {
                        if(!prefs.contains("state")) throw new IllegalStateException("Missing replay state; re-pair required");
                        out.putString("config",unwrap(prefs.getString("config","")));
                        out.putString("state",unwrap(prefs.getString("state","")));
                    }
                    out.putString("peerName",prefs.getString("peerName",""));out.putLong("lastSync",prefs.getLong("lastSync",0));out.putBoolean("paused",prefs.getBoolean("paused",false));out.putLong("moduleSeenAt",prefs.getLong("moduleSeenAt",0)); break;
                case "save":
                    if(arg==null || arg.length()>600000) throw new IllegalArgumentException();
                    JSONObject active=new JSONObject(unwrap(prefs.getString("config","")));
                    if(extras==null || !active.getString("pair").equals(extras.getString("pair")) || !active.getString("device").equals(extras.getString("device"))) throw new IllegalStateException("Pairing changed");
                    new JSONObject(arg);
                    if(!prefs.edit().putString("state",wrap(arg)).commit()) throw new IllegalStateException("Storage failed"); break;
                case "pause":
                    if(Binder.getCallingUid()!=android.os.Process.myUid()) throw new SecurityException();
                    if(!prefs.edit().putBoolean("paused",Boolean.parseBoolean(arg)).commit()) throw new IllegalStateException(); break;
                case "status":
                    if(arg!=null && arg.length()<120) prefs.edit().putString("status",arg).apply();
                    out.putString("status",prefs.getString("status","等待 SystemUI 模块启动")); break;
                case "heartbeat":
                    prefs.edit().putLong("moduleSeenAt",System.currentTimeMillis()).apply(); break;
                default: throw new IllegalArgumentException("Unknown operation");
            }
        } catch(SecurityException e){ throw e; }
        catch(Exception e){ throw new IllegalStateException("ClipBridge storage/configuration unavailable"); }
        return out;
    }
    public Cursor query(Uri u,String[] p,String s,String[] a,String o){ throw new SecurityException(); }
    public String getType(Uri u){ return null; }
    public Uri insert(Uri u,ContentValues v){ throw new SecurityException(); }
    public int delete(Uri u,String s,String[] a){ throw new SecurityException(); }
    public int update(Uri u,ContentValues v,String s,String[] a){ throw new SecurityException(); }
}
