package io.clipbridge.android;
import android.content.*;
import android.os.Bundle;
import org.json.*;
final class GroupController {
    private final Context context;
    GroupController(Context context){this.context=context;}
    static String deviceName(Context context){String name=android.provider.Settings.Global.getString(context.getContentResolver(),"device_name");return name==null||name.isBlank()?android.os.Build.MODEL:name;}
    JSONObject load(boolean initialize)throws Exception{Bundle b=context.getContentResolver().call(BridgeProvider.URI,"group-load",initialize?deviceName(context):null,null);return new JSONObject(new String(b.getByteArray("state"),java.nio.charset.StandardCharsets.UTF_8));}
    JSONObject save(JSONObject group)throws Exception{Bundle extras=new Bundle();extras.putByteArray("group",GroupCrypto.bytes(group.toString()));Bundle b=context.getContentResolver().call(BridgeProvider.URI,"group-save",null,extras);return new JSONObject(new String(b.getByteArray("state"),java.nio.charset.StandardCharsets.UTF_8));}
    void joined(JSONObject group)throws Exception{Bundle extras=new Bundle();extras.putByteArray("group",GroupCrypto.bytes(group.toString()));context.getContentResolver().call(BridgeProvider.URI,"group-join",null,extras);}
    void connect(String group,String peer)throws Exception{Bundle b=new Bundle();b.putString("group",group);b.putString("peer",peer);context.getContentResolver().call(BridgeProvider.URI,"group-connect",null,b);}
    boolean active()throws Exception{JSONObject state=load(false);if(!state.has("identity"))return false;String own=state.getJSONObject("identity").getJSONObject("member").getString("id");JSONArray groups=state.getJSONArray("groups");for(int i=0;i<groups.length();i++){JSONObject g=groups.getJSONObject(i);if(g.optLong("publishedSequence")!=g.optLong("sequence")||g.optBoolean("enabled",true)&&!GroupCrypto.contains(g.getJSONArray("removed"),own))return true;}return false;}
    void tick(boolean force)throws Exception{JSONObject state=load(false);if(!state.has("identity"))return;JSONObject identity=state.getJSONObject("identity");JSONArray groups=state.getJSONArray("groups");long now=System.currentTimeMillis()/1000;
        for(int i=0;i<groups.length();i++){JSONObject g=groups.getJSONObject(i);boolean changed=false;try{
            if(g.optLong("publishedSequence")!=g.getLong("sequence")&&g.optLong("publishRetryAfter")<=now){GroupNetwork.publish(g,identity);g.put("publishedSequence",g.getLong("sequence"));changed=true;}
            if(!g.optBoolean("enabled",true)||GroupCrypto.contains(g.getJSONArray("removed"),identity.getJSONObject("member").getString("id"))){String stopped=GroupCrypto.contains(g.getJSONArray("removed"),identity.getJSONObject("member").getString("id"))?"已退出同步组":"同步已关闭";if(!g.optString("status").equals(stopped)){g.put("status",stopped);changed=true;}if(changed)save(g);continue;}
            if(!force&&g.optLong("checkedAt")>now-60)continue;g.put("checkedAt",now);for(String author:new java.util.ArrayList<>(GroupCrypto.keys(g.getJSONObject("members")))){if(author.equals(identity.getJSONObject("member").getString("id"))||GroupCrypto.contains(g.getJSONArray("removed"),author))continue;JSONObject f=GroupNetwork.read(g,author);if(f!=null)changed|=GroupCrypto.accept(g,f);}g.put("status","成员清单已更新 · "+(g.getJSONObject("members").length()-g.getJSONArray("removed").length())+" 台设备");changed=true;
        }catch(Exception e){if(g.optLong("publishedSequence")!=g.getLong("sequence"))g.put("publishRetryAfter",now+30);g.put("checkedAt",now).put("status","成员信息暂不可用，稍后重试");changed=true;}if(changed)save(g);}
    }
}
