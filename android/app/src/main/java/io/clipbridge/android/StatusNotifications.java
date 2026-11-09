package io.clipbridge.android;
import android.app.*;
import android.content.*;
import android.os.Build;
import android.provider.Settings;
final class StatusNotifications {
    static final String CHANNEL="transfer-service-v2";
    static void ensure(Context context){NotificationChannel channel=new NotificationChannel(CHANNEL,"附件接收常驻提示",NotificationManager.IMPORTANCE_MIN);if(Build.VERSION.SDK_INT>=33)channel.setBlockable(true);context.getSystemService(NotificationManager.class).createNotificationChannel(channel);}
    static boolean visible(Context context){ensure(context);NotificationManager manager=context.getSystemService(NotificationManager.class);return manager.areNotificationsEnabled()&&manager.getNotificationChannel(CHANNEL).getImportance()!=NotificationManager.IMPORTANCE_NONE;}
    static Intent settings(Context context){ensure(context);return new Intent(Settings.ACTION_CHANNEL_NOTIFICATION_SETTINGS).putExtra(Settings.EXTRA_APP_PACKAGE,context.getPackageName()).putExtra(Settings.EXTRA_CHANNEL_ID,CHANNEL);}
}
