package io.clipbridge.android;
import org.json.*;
public final class GroupSessionBackTest {
    static int count;
    static void check(boolean ok,String name){if(!ok)throw new AssertionError(name);count++;System.out.println("PASS "+name);}
    public static void main(String[] args)throws Exception{
        JSONObject a=GroupCrypto.identity(GroupCrypto.id(),"Host"),b=GroupCrypto.identity(GroupCrypto.id(),"Guest");
        JSONObject c=new JSONObject().put("transport","relay").put("server","https://example.com");
        JSONObject group=GroupCrypto.create(a,"Test group",c);String guestId=b.getJSONObject("member").getString("id");
        int[] saved={0},installed={0};boolean[] failInstall={true};JSONObject guestState=GroupStateOps.empty().put("identity",b);
        GroupSession host=GroupSession.invite(group,a,()->saved[0]++);
        GroupSession guest=GroupSession.join(c,host.code,b,g->{if(failInstall[0])throw new java.io.IOException("disk unavailable");GroupStateOps.install(guestState,g);installed[0]++;});
        check(host.canClose()&&guest.canClose(),"unapproved session can be cancelled");
        for(int i=0;i<20&&(!host.ready()||!guest.ready());i++){host.step();guest.step();}
        check(host.ready()&&guest.ready()&&host.sas.equals(guest.sas),"real PAKE reaches matching safety codes");
        host.approve();guest.approve();check(!host.canClose()&&!guest.canClose(),"back and close are blocked after approval");
        host.step();guest.step();host.step();
        check(!group.getJSONObject("members").has(guestId)&&saved[0]==0&&GroupNetwork.published==null,"grant does not save or publish a provisional member");
        try{guest.step();throw new AssertionError("expected disk failure");}catch(java.io.IOException expected){}
        host.step();check(!host.done&&!group.getJSONObject("members").has(guestId),"guest save failure cannot commit a ghost member");
        failInstall[0]=false;guest.step();check(guest.done&&installed[0]==1&&guestState.getJSONArray("groups").length()==1,"guest stores the group before sending completion");
        host.step();check(host.done&&saved[0]==1&&group.getJSONObject("members").has(guestId)&&GroupNetwork.published.getJSONObject("members").has(guestId),"host commits and publishes only after durable guest completion");
        check(host.canClose()&&guest.canClose(),"completed session can be closed");host.close();guest.close();
        JSONObject visible=GroupCrypto.create(a,"Visible group",c),state=GroupStateOps.empty().put("identity",a);state.getJSONArray("groups").put(visible);
        String orphan=GroupCrypto.id();JSONObject stones=new JSONObject();JSONArray rows=new JSONArray();
        JSONObject route=new JSONObject().put("groupId",orphan).put("config",new JSONObject().put("pair",GroupCrypto.id())).put("state",new JSONObject().put("clock",42).put("winner","saved").put("pending",new JSONObject()));rows.put(route);
        JSONObject kept=new JSONObject().put("groupId",visible.getString("id")).put("config",new JSONObject().put("pair",GroupCrypto.id())).put("state",new JSONObject());rows.put(kept);
        JSONObject legacy=new JSONObject().put("config",new JSONObject().put("pair",GroupCrypto.id())).put("state",new JSONObject());rows.put(legacy);
        visible.getJSONObject("connection").put("transport","relay");kept.getJSONObject("config").put("transport","webdav");GroupStateOps.describeTransport(state,kept);check(kept.getString("connectionTransport").equals("relay"),"relay storage adapter does not masquerade as WebDAV in profile metadata");
        JSONObject preview=GroupStateOps.hiddenGroups(state,rows);check(preview.getJSONArray("groups").length()==1&&preview.getInt("connections")==1&&rows.length()==3,"cleanup preview finds only orphan group routes without mutation");
        check(GroupStateOps.cleanHidden(state,rows,stones)==1&&rows.length()==2&&rows.getJSONObject(0)==kept&&rows.getJSONObject(1)==legacy,"cleanup preserves displayed groups and legacy routes");
        JSONObject replay=stones.getJSONObject(route.getJSONObject("config").getString("pair"));check(replay.getLong("clock")==42&&!replay.has("pending"),"cleanup preserves replay protection and drops pending sends");
        check(GroupCrypto.contains(state.getJSONArray("deletedGroups"),orphan),"deleted hidden group cannot be recreated by late save");
        check(GroupStateOps.cleanHidden(state,rows,stones)==0,"cleanup is idempotent");
        System.out.println("PASS "+count+" group completion and cleanup regression checks");
    }
}
