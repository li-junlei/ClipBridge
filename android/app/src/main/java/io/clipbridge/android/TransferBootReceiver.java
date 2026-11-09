package io.clipbridge.android;
import android.content.*;
public final class TransferBootReceiver extends BroadcastReceiver {
 public void onReceive(Context c,Intent intent){if(!Intent.ACTION_BOOT_COMPLETED.equals(intent.getAction()))return;try{if(android.os.Build.VERSION.SDK_INT>=33&&c.checkSelfPermission(android.Manifest.permission.POST_NOTIFICATIONS)!=android.content.pm.PackageManager.PERMISSION_GRANTED)return;org.json.JSONArray profiles=new org.json.JSONArray(c.getContentResolver().call(BridgeProvider.URI,"profiles",null,null).getString("profiles"));if(profiles.length()>0)c.startForegroundService(new Intent(c,TransferService.class));}catch(Exception ignored){}}
}
