package io.clipbridge.android;
import org.json.*;
import java.nio.charset.StandardCharsets;
import java.io.*;
import java.util.*;
import okhttp3.*;
final class MediaWire {
    private final JSONObject config;private final String origin,authorization;private final boolean dav;private final Set<String> created=new HashSet<>();private boolean capable;
    private static final OkHttpClient HTTP=new OkHttpClient.Builder().followRedirects(false).followSslRedirects(false).connectTimeout(10,java.util.concurrent.TimeUnit.SECONDS).readTimeout(35,java.util.concurrent.TimeUnit.SECONDS).callTimeout(45,java.util.concurrent.TimeUnit.SECONDS).build();
    MediaWire(JSONObject c)throws Exception{config=c;Protocol.config(c);origin=c.getString("server").replaceAll("/+$","")+"/";dav="webdav".equals(c.optString("transport"));authorization=dav?"Basic "+Protocol.encode((c.getString("username")+":"+c.getString("password")).getBytes(StandardCharsets.UTF_8)):"Bearer "+c.getString("token");}
    private String path(String device,String key)throws Exception{return dav?"clipbridge/"+config.getString("pair")+"/"+(key.equals("index")?device+".transfers.json":"attachments/"+device+"/"+key+".bin"):"v1/objects/"+device+"/"+key;}
    private void ensure(boolean blob)throws Exception{if(!dav)return;String base="clipbridge/"+config.getString("pair")+"/";for(String path:new String[]{"clipbridge/",base,blob?base+"attachments/":"",blob?base+"attachments/"+config.getString("device")+"/":""})if(!path.isEmpty()&&!created.contains(path)){exchange("MKCOL",path,null);created.add(path);}}
    private void capability()throws Exception{if(dav||capable)return;byte[] data=exchange("GET","v1/setup/info",null);if(data==null||new JSONObject(new String(data,StandardCharsets.UTF_8)).optInt("attachments")!=1)throw new IOException("Update relay for attachments");capable=true;}
    JSONObject index()throws Exception{capability();byte[] data=exchange("GET",path(config.getString("peer"),"index"),null);return data==null?null:MediaProtocol.openIndex(config,new String(data,StandardCharsets.UTF_8));}
    void index(JSONObject index)throws Exception{capability();ensure(false);exchange("PUT",path(config.getString("device"),"index"),MediaProtocol.sealIndex(config,index).getBytes(StandardCharsets.UTF_8));}
    void put(String id,int part,byte[] plain)throws Exception{capability();ensure(true);exchange("PUT",path(config.getString("device"),id+"-"+part),MediaProtocol.encrypt(config,id,part,plain));}
    byte[] get(String id,int part)throws Exception{capability();byte[] bytes=exchange("GET",path(config.getString("peer"),id+"-"+part),null);if(bytes==null)throw new IOException("Not ready");return MediaProtocol.decrypt(config,id,part,bytes);}
    void delete(String id,int part)throws Exception{exchange("DELETE",path(config.getString("device"),id+"-"+part),null);}
    private byte[] exchange(String method,String path,byte[] body)throws Exception{RequestBody data=body==null?null:RequestBody.create(body,MediaType.parse("application/octet-stream"));Request request=new Request.Builder().url(origin+path).method(method,data).header("Authorization",authorization).header("Cache-Control","no-store, no-cache").build();try(Response response=HTTP.newCall(request).execute()){if((method.equals("GET")||method.equals("DELETE"))&&response.code()==404)return null;if(method.equals("MKCOL")&&response.code()==405)return null;if(!response.isSuccessful())throw new IOException("Transfer HTTP "+response.code());if(!method.equals("GET"))return new byte[0];if(response.body()==null)throw new IOException();try(InputStream input=response.body().byteStream();ByteArrayOutputStream output=new ByteArrayOutputStream()){byte[] buffer=new byte[8192];int n;while((n=input.read(buffer))!=-1){if(output.size()+n>600000)throw new IOException("Oversized transfer response");output.write(buffer,0,n);}return output.toByteArray();}}}
}
