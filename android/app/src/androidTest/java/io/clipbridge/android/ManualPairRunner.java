package io.clipbridge.android;
import android.app.*;
import android.content.*;
import android.os.*;
import org.json.*;
import java.io.*;
import java.nio.charset.StandardCharsets;
public class ManualPairRunner extends Instrumentation {
    private Bundle arguments;private Activity activity;private Context context;
    public void onCreate(Bundle args){arguments=args;super.onCreate(args);}
    private void status(String text){Bundle b=new Bundle();b.putString("stream",text+"\n");sendStatus(0,b);}
    @SuppressWarnings("unchecked") private void state(String name,String value)throws Exception{java.lang.reflect.Field field=activity.getClass().getDeclaredField(name+"$delegate");field.setAccessible(true);((androidx.compose.runtime.MutableState<String>)field.get(activity)).setValue(value);}
    private Object field(String name)throws Exception{java.lang.reflect.Field f=activity.getClass().getDeclaredField(name);f.setAccessible(true);return f.get(activity);}
    private void call(String name)throws Exception{java.lang.reflect.Method m=activity.getClass().getDeclaredMethod(name);m.setAccessible(true);m.invoke(activity);}
    public void onStart(){Bundle result=new Bundle();boolean ok=false;try{
        if(!"ranchu".equals(Build.HARDWARE)&&!"goldfish".equals(Build.HARDWARE))throw new AssertionError("Dedicated emulator required");context=getTargetContext();if(new JSONArray(context.getContentResolver().call(BridgeProvider.URI,"profiles",null,null).getString("profiles")).length()!=0)throw new AssertionError("Empty emulator required");
        activity=startActivitySync(new Intent().setClassName(context,"io.clipbridge.android.GroupActivity").putExtra("mode",2).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK));waitForIdleSync();
        runOnMainSync(()->{try{state("server",arguments.getString("server"));state("invitation",GroupPake.display(arguments.getString("code")).toLowerCase(java.util.Locale.ROOT));call("join");}catch(Exception e){throw new RuntimeException(e);}});
        long end=System.currentTimeMillis()+150000;boolean ready=false;File directory=context.getExternalFilesDir(null),expected=new File(directory,"manual-host-sas.txt");
        while(System.currentTimeMillis()<end){GroupSession session=(GroupSession)field("session");if(session!=null){if(session.ready()&&!session.approved){if(!ready){status("MANUAL_READY "+session.sas);ready=true;}if(expected.exists()){if(!java.nio.file.Files.readString(expected.toPath()).trim().equals(session.sas))throw new AssertionError("Safety code differs");runOnMainSync(()->{try{call("approve");}catch(Exception e){throw new RuntimeException(e);}});}}if(session.done){GroupController controller=new GroupController(context);JSONObject document=controller.load(false),identity=document.getJSONObject("identity"),group=document.getJSONArray("groups").getJSONObject(0),peer=session.peer();JSONObject config=GroupCrypto.channel(group,identity,peer);java.nio.file.Files.writeString(new File(directory,"manual-result.json").toPath(),new JSONObject().put("member",identity.getJSONObject("member")).put("config",config).toString());status("PASS Android activity joins using typed server and displayed code");GroupCrypto.remove(group,identity,identity.getJSONObject("member").getString("id"));controller.save(group);context.getContentResolver().call(BridgeProvider.URI,"group-delete",group.getString("id"),null);ok=true;break;}}
            Thread.sleep(150);
        }
        if(!ok)throw new AssertionError("Manual join timed out: "+((androidx.compose.runtime.MutableState<?>)field("message$delegate")).getValue());result.putString("stream","ANDROID_MANUAL_PAIRING_PASSED\n");
    }catch(Throwable e){result.putString("stream","FAIL "+e.getClass().getSimpleName()+": "+e.getMessage()+"\n");}finally{if(context!=null){context.stopService(new Intent(context,TransferService.class));File directory=context.getExternalFilesDir(null);if(directory!=null)new File(directory,"manual-host-sas.txt").delete();}if(activity!=null)runOnMainSync(activity::finish);finish(ok?Activity.RESULT_OK:Activity.RESULT_CANCELED,result);}}
}
