package io.clipbridge.android;
import android.app.Activity;
import android.content.*;
import android.net.Uri;
import android.os.Bundle;
import android.webkit.MimeTypeMap;
import android.widget.Toast;
public final class OpenReceivedActivity extends Activity {
    @Override public void onCreate(Bundle saved){super.onCreate(saved);try{
        Uri uri=getIntent().getData();if(uri==null||!"content".equals(uri.getScheme()))throw new IllegalArgumentException();String mime=getIntent().getStringExtra("mime"),name=getIntent().getStringExtra("name");if(mime==null||mime.equals("application/octet-stream")){String extension=name!=null&&name.contains(".")?name.substring(name.lastIndexOf('.')+1).toLowerCase(java.util.Locale.ROOT):"";mime=MimeTypeMap.getSingleton().getMimeTypeFromExtension(extension);if(mime==null)mime="*/*";}
        startActivity(new Intent(Intent.ACTION_VIEW).setDataAndType(uri,mime).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION));
    }catch(ActivityNotFoundException e){Toast.makeText(this,"未找到可打开此文件的应用，请安装对应应用后重试。",Toast.LENGTH_LONG).show();}catch(Exception e){Toast.makeText(this,"文件无法打开，可能已被移动或删除。",Toast.LENGTH_LONG).show();}finally{finish();}}
}
