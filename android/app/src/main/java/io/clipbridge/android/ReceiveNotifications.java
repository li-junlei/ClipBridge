package io.clipbridge.android;
import android.app.*;
import android.content.Context;
import android.graphics.Bitmap;
import android.view.View;
import android.widget.RemoteViews;

final class ReceiveNotifications {
    static Notification build(Context context,boolean compact,String name,String title,String detail,
            boolean working,boolean determinate,long bytes,long size,PendingIntent open,
            PendingIntent accept,PendingIntent reject,boolean preview,Bitmap bitmap){
        Notification.Builder builder=new Notification.Builder(context,"transfers")
            .setSmallIcon(R.drawable.ic_clipbridge_foreground).setContentTitle(title)
            .setContentText(name+" · "+detail).setContentIntent(open).setAutoCancel(false)
            .setOnlyAlertOnce(true).setOngoing(working);
        if(working)builder.setProgress(10000,ReceiveProgress.percent(bytes,size),!determinate);
        if(compact){
            builder.setStyle(new Notification.DecoratedCustomViewStyle())
                .setCustomContentView(layout(context,R.layout.receive_compact,name,title,detail,working,determinate,bytes,size,accept,reject,preview,null))
                .setCustomBigContentView(layout(context,R.layout.receive_expanded,name,title,detail,working,determinate,bytes,size,accept,reject,preview,bitmap))
                .setCustomHeadsUpContentView(layout(context,R.layout.receive_compact,name,title,detail,working,determinate,bytes,size,accept,reject,preview,null));
        }else{
            if(accept!=null)builder.addAction(new Notification.Action.Builder(null,preview?"保存":"接收",accept).build());
            if(reject!=null)builder.addAction(new Notification.Action.Builder(null,preview?"丢弃":"拒绝",reject).build());
            if(bitmap!=null)builder.setStyle(new Notification.BigPictureStyle().bigPicture(bitmap));
            else builder.setStyle(new Notification.BigTextStyle().bigText(name+"\n"+detail));
        }
        return builder.build();
    }
    private static RemoteViews layout(Context context,int resource,String name,String title,String detail,
            boolean working,boolean determinate,long bytes,long size,PendingIntent accept,
            PendingIntent reject,boolean preview,Bitmap bitmap){
        RemoteViews views=new RemoteViews(context.getPackageName(),resource);
        views.setTextViewText(R.id.receive_title,working?title+" · "+(determinate?ReceiveProgress.percent(bytes,size)/100+"%":"等待中"):title);
        views.setTextViewText(R.id.receive_name,name);
        views.setViewVisibility(R.id.receive_progress,working?View.VISIBLE:View.GONE);
        views.setProgressBar(R.id.receive_progress,10000,ReceiveProgress.percent(bytes,size),!determinate);
        views.setViewVisibility(R.id.receive_accept,accept==null?View.GONE:View.VISIBLE);
        views.setViewVisibility(R.id.receive_reject,reject==null?View.GONE:View.VISIBLE);
        if(accept!=null){views.setTextViewText(R.id.receive_accept,preview?"保存":"接收");views.setOnClickPendingIntent(R.id.receive_accept,accept);}
        if(reject!=null){views.setTextViewText(R.id.receive_reject,preview?"丢弃":"拒绝");views.setOnClickPendingIntent(R.id.receive_reject,reject);}
        if(resource==R.layout.receive_expanded){
            views.setTextViewText(R.id.receive_detail,detail);
            views.setViewVisibility(R.id.receive_image,bitmap==null?View.GONE:View.VISIBLE);
            if(bitmap!=null)views.setImageViewBitmap(R.id.receive_image,bitmap);
        }
        return views;
    }
}
