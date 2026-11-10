package io.clipbridge.android;

import android.content.*;
import android.os.*;
import org.json.JSONObject;
import javax.net.ssl.HttpsURLConnection;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.util.concurrent.*;
import java.util.concurrent.atomic.*;

final class Engine {
    private final Context context;
    private final ClipboardManager clipboard;
    private final Handler main=new Handler(Looper.getMainLooper());
    private final ScheduledExecutorService worker=Executors.newSingleThreadScheduledExecutor(r->{Thread t=new Thread(r,"ClipBridge-worker");t.setDaemon(true);return t;});
    private static final class LocalClip{final String text,event;final long created,clock;LocalClip(String text,String event){this.text=text;this.event=event;created=EngineRouter.created();clock=EngineRouter.clock();}}
    private final AtomicReference<LocalClip> local=new AtomicReference<>();
    private final AtomicReference<JSONObject> remote=new AtomicReference<>();
    private final AtomicLong generation=new AtomicLong();
    private volatile JSONObject config;
    private final Object pollWake=new Object();private volatile boolean paused=true;private volatile int pollSeconds=2;
    private volatile String lastObserved;
    private JSONObject state;
    private long nextLoad=0,nextSend=0,nextIdentity=0;
    private int failures=0;
    private volatile WebDav dav;
    private ClipboardManager.OnPrimaryClipChangedListener listener;
    private final String pair;private volatile boolean running=true;private String installation="";
    Engine(Context c,String pair){this.pair=pair;context=c;clipboard=(ClipboardManager)c.getSystemService(Context.CLIPBOARD_SERVICE);}
    boolean ready(){return running&&config!=null&&!paused;}
    void queue(String text,String event){local.set(new LocalClip(text,event));generation.incrementAndGet();}
    void stop(){running=false;synchronized(pollWake){pollWake.notifyAll();}paused=true;worker.shutdownNow();if(listener!=null)main.post(()->clipboard.removePrimaryClipChangedListener(listener));config=null;}
    void start(){
        if(!running)return;
        listener=()->{
            try {
                if(paused || config==null) return;
                ClipData clip=clipboard.getPrimaryClip();
                if(clip==null || clip.getItemCount()!=1 || "ClipBridge".equals(String.valueOf(clip.getDescription().getLabel()))) return;
                PersistableBundle extras=clip.getDescription().getExtras();
                if(extras!=null && extras.getBoolean("android.content.extra.IS_SENSITIVE",false)) return;
                // Never coerce URI/file clips into text; that may perform provider I/O inside SystemUI.
                CharSequence text=clip.getItemAt(0).getText();
                if(text==null || text.length()>65536) return;
                String s=text.toString();
                if(s.regionMatches(true,0,"clipbridge://",0,13))return;
                if(s.equals(lastObserved)) return;
                lastObserved=s;EngineRouter.copy(s);
            }catch(Throwable ignored){}
        };clipboard.addPrimaryClipChangedListener(listener);
        worker.scheduleWithFixedDelay(()->{try{tick();}catch(Throwable e){status("同步异常，稍后重试");}},0,300,TimeUnit.MILLISECONDS);
        Thread receiver=new Thread(this::receiveLoop,"ClipBridge-receiver");receiver.setDaemon(true);receiver.start();
    }
    private void status(String s){try{context.getContentResolver().call(BridgeProvider.URI,"status",s,identityBundle());}catch(Throwable ignored){}}
    private void save(JSONObject next) throws Exception {
        Bundle identity=new Bundle();identity.putString("pair",config.getString("pair"));identity.putString("device",config.getString("device"));
        identity.putByteArray("state",next.toString().getBytes(StandardCharsets.UTF_8));context.getContentResolver().call(BridgeProvider.URI,"save",null,identity);state=next;
    }
    private void tick() throws Exception {
        long now=System.currentTimeMillis();
        if(now>=nextLoad){
            nextLoad=now+5000;
            try {
                Bundle b=context.getContentResolver().call(BridgeProvider.URI,"load",pair,null);

                int newPoll=Math.max(1,Math.min(300,b.getInt("textPollSeconds",2)));if(newPoll!=pollSeconds){synchronized(pollWake){pollSeconds=newPoll;pollWake.notifyAll();}}paused=b.getBoolean("paused");installation=b.getString("installation","");
                if(!b.containsKey("config")){paused=true;config=null;state=null;local.set(null);remote.set(null);generation.incrementAndGet();lastObserved=null;return;}
                if(b.containsKey("config") && (config==null || !config.toString().equals(b.getString("config")))){
                    JSONObject c=new JSONObject(b.getString("config"));Protocol.config(c);
                    JSONObject saved=new JSONObject(new String(b.getByteArray("state"),StandardCharsets.UTF_8));Protocol.state(c,saved);
                    local.set(null);remote.set(null);generation.incrementAndGet();
                    dav="webdav".equals(c.optString("transport"))?new WebDav(c.getString("server"),c.getString("username"),c.getString("password"),c.getString("pair")):null;
                    state=saved;config=c;if(saved.has("pending")&&!saved.optString("pendingEvent").isEmpty()){JSONObject reverse=new JSONObject(c.toString()).put("device",c.getString("peer")).put("peer",c.getString("device"));try{JSONObject payload=Protocol.payload(reverse,saved.getJSONObject("pending"));EngineRouter.restore(pair,saved.getString("pendingEvent"),payload.getString("text"),payload.getLong("created"),payload.optLong("eventClock"),saved.optString("pendingTargets","[]"));}catch(Exception ignored){}}
                    nextSend=0;nextIdentity=0;lastObserved=null;status("模块已连接，等待剪贴板变化");
                }
            } catch(Exception e){paused=true;status("配置暂不可用；解锁后自动重试");return;}
        }
        JSONObject c=config;if(c==null || paused) return;
        if(now>=nextIdentity){nextIdentity=now+("webdav".equals(c.optString("transport"))?300000:15000);try{
            JSONObject nameChannel=identityConfig(c);
            request(c,"POST","v1/device-info",Protocol.seal(nameChannel,System.currentTimeMillis(),new JSONObject().put("name",deviceName()).put("installation",installation).toString()).toString());
            String data=request(c,"GET","v1/device-info",null);
            if(!data.trim().equals("null")){String name=Protocol.open(nameChannel,new JSONObject(data));if(name.startsWith("{")){JSONObject peer=new JSONObject(name);String peerId=peer.getString("installation");Protocol.id(peerId);metrics(c,"peerIdentity",peerId);name=peer.getString("name");}if(name.length()>80 || name.isBlank() || name.matches(".*[\\p{Cntrl}].*"))throw new SecurityException();metrics(c,"peerName",name);}
        }catch(Exception ignored){}}
        LocalClip clip=local.getAndSet(null);String text=clip==null?null:clip.text;
        if(text!=null && text.getBytes(StandardCharsets.UTF_8).length>65536){
            status("文本超过 64 KiB，已跳过");
        } else if(text!=null){
            try{
                long clock=Math.addExact(state.getLong("clock"),1);
                String event=clip.event;JSONObject envelope=Protocol.seal(c,clock,text,event,installation,clip.created,clip.clock);
                JSONObject next=new JSONObject().put("clock",clock).put("winner",c.getString("device")).put("pending",envelope).put("pendingEvent",event).put("pendingTargets",EngineRouter.targetList());
                save(next);
            }catch(Exception e){local.compareAndSet(null,clip);throw e;}
        }
        JSONObject incoming=remote.getAndSet(null);
        if(incoming!=null){
            try {
                String content=Protocol.open(c,incoming);
                JSONObject payload=Protocol.payload(c,incoming);String incomingEvent=payload.optString("eventId");
                if(!incomingEvent.isEmpty()){Bundle seen=identityBundle();seen.putString("origin",payload.getString("origin"));seen.putLong("eventClock",payload.optLong("eventClock"));if(!context.getContentResolver().call(BridgeProvider.URI,"seen",incomingEvent,seen).getBoolean("first"))return;}
                long clock=incoming.getLong("clock"),current=state.getLong("clock");String from=incoming.getString("from");
                if(clock>current || (clock==current && from.compareTo(state.getString("winner"))>0)){
                    long expected=generation.get();
                    EngineRouter.success(pair,state.optString("pendingEvent"));save(new JSONObject().put("clock",clock).put("winner",from));
                    main.post(()->{
                        try{
                            if(paused || generation.get()!=expected || !isCurrent(c)) return;
                            lastObserved=content;EngineRouter.received(content);
                            ClipData currentClip=clipboard.getPrimaryClip();boolean same=currentClip!=null&&currentClip.getItemCount()==1&&content.contentEquals(currentClip.getItemAt(0).getText()==null?"":currentClip.getItemAt(0).getText());if(!same)clipboard.setPrimaryClip(ClipData.newPlainText("ClipBridge",content));
                            worker.execute(()->{if(config==c){metrics(c,"lastSync",System.currentTimeMillis());status("已接收并写入剪贴板");}});
                        }catch(Throwable e){worker.execute(()->status("远端内容已接收，但剪贴板写入失败"));}
                    });
                }
            }catch(Exception e){status("已拒绝消息或无法保存接收状态");}
        }
        if(state.has("pending") && now>=nextSend){
            JSONObject pending=state.getJSONObject("pending");String pendingEvent=state.optString("pendingEvent");
            if(!EngineRouter.owns(pair,pendingEvent)){JSONObject clean=new JSONObject(state.toString());clean.remove("pending");save(clean);return;}
            try{
                request(c,"POST","v1/messages",pending.toString());
                JSONObject next=new JSONObject(state.toString());next.remove("pending");save(next);failures=0;nextSend=now+1000;
                EngineRouter.success(pair,pendingEvent);metrics(c,"lastSync",System.currentTimeMillis());status("已发送");
            }catch(Exception e){EngineRouter.failure(pair,pendingEvent);JSONObject clean=new JSONObject(state.toString());clean.remove("pending");save(clean);failures=Math.min(failures+1,6);nextSend=now+Math.min(60000,(1L<<failures)*1000);status("发送失败，等待重试");}
        }
    }
    private void receiveLoop(){
        String after="";int failures=0;
        while(running){
            try{
                JSONObject c=config;
                if(c==null || paused){Thread.sleep(1000);continue;}
                String response=request(c,"GET","v1/inbox?after="+after,null);
                if(response.trim().equals("null") && !paused && config==c) status("连接正常，等待新内容");
                if(!response.trim().equals("null")){
                    JSONObject e=new JSONObject(response);Protocol.header(e);
                    if(running && !paused && config==c){
                        // Validate before handing off. Untrusted traffic cannot fill a work queue.
                        if(!e.getString("id").equals(after)){
                            try{Protocol.open(c,e);remote.set(e);}catch(Protocol.ExpiredMessage ignored){}
                            after=e.getString("id");
                        }
                    }
                }
                failures=0;
            }catch(Throwable e){failures=Math.min(failures+1,6);status("接收连接异常或消息被拒绝");}
            try{synchronized(pollWake){if(running)pollWake.wait(failures==0 && config!=null && "webdav".equals(config.optString("transport"))?pollSeconds*1000L:Math.min(60000,(1L<<failures)*1000));}}catch(InterruptedException e){return;}
        }
    }
    private Bundle identityBundle(){Bundle b=new Bundle();b.putString("pair",pair);return b;}
    private String deviceName(){String name=android.provider.Settings.Global.getString(context.getContentResolver(),"device_name");if(name==null || name.isBlank())name=android.os.Build.MODEL;name=name.replaceAll("[\\p{Cntrl}]","");return name.substring(0,Math.min(80,name.length()));}
    private boolean isCurrent(JSONObject c){try{Bundle loaded=context.getContentResolver().call(BridgeProvider.URI,"load",pair,null);if(!loaded.containsKey("config") || loaded.getBoolean("paused"))return false;JSONObject active=new JSONObject(loaded.getString("config"));return active.getString("pair").equals(c.getString("pair")) && active.getString("device").equals(c.getString("device"));}catch(Exception e){return false;}}
    private JSONObject identityConfig(JSONObject c) throws Exception {
        javax.crypto.Mac mac=javax.crypto.Mac.getInstance("HmacSHA256");mac.init(new javax.crypto.spec.SecretKeySpec(Protocol.decode(c.getString("key")),"HmacSHA256"));
        return new JSONObject(c.toString()).put("key",Protocol.encode(mac.doFinal("clipbridge:device-name:1".getBytes(StandardCharsets.US_ASCII))));
    }
    private void metrics(JSONObject c,String key,Object value){try{Bundle b=new Bundle();b.putString("pair",c.getString("pair"));b.putString("device",c.getString("device"));if(value instanceof Long)b.putLong(key,(Long)value);else b.putString(key,(String)value);context.getContentResolver().call(BridgeProvider.URI,"metrics",null,b);}catch(Exception ignored){}}
    private String request(JSONObject c,String method,String path,String body) throws Exception {
        if(method.equals("POST") && path.equals("v1/messages") && !isCurrent(c))throw new IllegalStateException("Pairing inactive");
        if("webdav".equals(c.optString("transport"))){
            WebDav current=dav;if(current==null || config!=c)throw new IllegalStateException();
            boolean identity=path.equals("v1/device-info");String device=method.equals("POST")?c.getString("device"):c.getString("peer");String file=device+(identity?".device.json":".clip.json");
            if(method.equals("POST")){current.put(file,body);return "null";}return current.get(file);
        }
        String origin=c.getString("server");
        HttpsURLConnection connection=(HttpsURLConnection)new URL(origin.replaceAll("/+$", "")+"/"+path).openConnection();
        connection.setInstanceFollowRedirects(false);connection.setConnectTimeout(10000);connection.setReadTimeout(30000);
        connection.setRequestProperty("Authorization","Bearer "+c.getString("token"));connection.setRequestMethod(method);
        try{
            if(body!=null){connection.setDoOutput(true);connection.setRequestProperty("Content-Type","application/json");byte[] bytes=body.getBytes(StandardCharsets.UTF_8);connection.setFixedLengthStreamingMode(bytes.length);try(java.io.OutputStream out=connection.getOutputStream()){out.write(bytes);}}
            int code=connection.getResponseCode();if(code!=200)throw new java.io.IOException("Relay HTTP "+code);
            try(java.io.InputStream in=connection.getInputStream();java.io.ByteArrayOutputStream out=new java.io.ByteArrayOutputStream()){
                byte[] buffer=new byte[4096];int n;
                while((n=in.read(buffer))!=-1){out.write(buffer,0,n);if(out.size()>600000)throw new java.io.IOException("Oversized relay response");}
                return out.toString(StandardCharsets.UTF_8.name());
            }
        }finally{connection.disconnect();}
    }
}
