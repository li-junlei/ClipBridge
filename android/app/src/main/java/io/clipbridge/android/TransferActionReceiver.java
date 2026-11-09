package io.clipbridge.android;
import android.content.*;
public final class TransferActionReceiver extends BroadcastReceiver {
 public void onReceive(Context context,Intent intent){String id=intent.getStringExtra("id");if(id==null||!id.matches("[0-9a-f]{32}"))return;context.startForegroundService(new Intent(context,TransferService.class).putExtra("action","decide").putExtra("id",id).putExtra("accept",intent.getBooleanExtra("accept",false)));}
}
