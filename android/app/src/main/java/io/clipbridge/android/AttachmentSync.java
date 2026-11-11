package io.clipbridge.android;

import org.json.*;
import java.util.Set;

// Attachment housekeeping continues in the background, but only live tasks warrant alerts.
final class AttachmentSync {
    static boolean shouldPublish(JSONObject index,boolean advertised)throws Exception {
        return advertised||index.getJSONArray("offers").length()>0||index.getJSONArray("decisions").length()>0;
    }
    static boolean pending(JSONObject state,String pair,long now)throws Exception {
        for(String direction:new String[]{"outgoing","incoming"}) {
            JSONArray jobs=state.getJSONArray(direction);
            for(int i=0;i<jobs.length();i++) {
                JSONObject job=jobs.getJSONObject(i);
                if(job.getJSONObject("offer").getLong("expires")<=now)continue;
                String phase=job.optString("phase");
                if(direction.equals("outgoing")?Set.of("done","rejected","cancelled").contains(phase):!Set.of("waiting","accepted").contains(phase))continue;
                JSONArray pairs=job.getJSONArray("pairs");
                for(int n=0;n<pairs.length();n++)if(pair.equals(pairs.getString(n)))return true;
            }
        }
        return false;
    }
    static boolean isWebDav(JSONObject profile)throws Exception{return "webdav".equals(profile.optString("connectionTransport",profile.getJSONObject("config").optString("transport","relay")));}
    static String label(JSONObject profile)throws Exception {
        String name=profile.optString("name").replaceAll("[\\p{Cntrl}]", " ").trim();
        if(name.isEmpty())name="连接 "+profile.getJSONObject("config").getString("pair").substring(0,8);
        if(name.length()>40)name=name.substring(0,40);
        return name+" · "+(isWebDav(profile)?"WebDAV ":"unknown".equals(profile.optString("connectionTransport"))?"附件连接 ":"专用中转 ");
    }
}
