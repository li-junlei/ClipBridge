package io.clipbridge.android;

import java.net.*;
import java.nio.charset.StandardCharsets;
import java.util.Base64;
import java.io.*;

// No global authenticator or redirect following: credentials stay on the configured server.
final class WebDav {
    private final String origin,authorization,directory;
    private boolean ready;
    WebDav(String origin,String username,String password,String pair){this.origin=origin.replaceAll("/+$", "")+"/";authorization="Basic "+Base64.getEncoder().encodeToString((username+":"+password).getBytes(StandardCharsets.UTF_8));directory="clipbridge/"+pair+"/";}
    synchronized void put(String file,String body)throws Exception{
        if(!ready){exchange("MKCOL","clipbridge/",null);exchange("MKCOL",directory,null);ready=true;}
        try{exchange("PUT",directory+file,body);}catch(Exception e){ready=false;throw e;}
    }
    String get(String file)throws Exception{return exchange("GET",directory+file,null);}
    private static final okhttp3.OkHttpClient HTTP=new okhttp3.OkHttpClient.Builder().followRedirects(false).followSslRedirects(false).connectTimeout(10,java.util.concurrent.TimeUnit.SECONDS).readTimeout(15,java.util.concurrent.TimeUnit.SECONDS).callTimeout(25,java.util.concurrent.TimeUnit.SECONDS).build();
    private String exchange(String method,String path,String body)throws Exception{
        okhttp3.RequestBody payload=body==null?null:okhttp3.RequestBody.create(body,okhttp3.MediaType.parse("application/json; charset=utf-8"));
        okhttp3.Request request=new okhttp3.Request.Builder().url(origin+path).method(method,payload).header("Authorization",authorization).header("Cache-Control","no-cache, no-store").build();
        try(okhttp3.Response response=HTTP.newCall(request).execute()){
            int code=response.code();
            if(method.equals("GET") && code==404)return "null";
            if(method.equals("MKCOL") && code==405)return "null";
            if(!response.isSuccessful())throw new IOException("WebDAV HTTP "+code);
            if(!method.equals("GET"))return "null";
            if(response.body()==null)throw new IOException("Missing response");
            try(InputStream in=response.body().byteStream();ByteArrayOutputStream out=new ByteArrayOutputStream()){
                byte[] buffer=new byte[4096];int n;while((n=in.read(buffer))!=-1){out.write(buffer,0,n);if(out.size()>600000)throw new IOException("Oversized response");}return out.toString(StandardCharsets.UTF_8.name());
            }
        }
    }
}
