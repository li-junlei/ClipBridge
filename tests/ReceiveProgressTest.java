package io.clipbridge.android;
import org.json.*;
import java.io.*;
import java.net.*;
import java.nio.file.*;
import java.util.*;
import com.sun.net.httpserver.HttpServer;
public final class ReceiveProgressTest {
    static int count;
    static void check(boolean value,String name){if(!value)throw new AssertionError(name);count++;System.out.println("PASS "+name);}
    public static void main(String[] args)throws Exception{
        check(ReceiveProgress.bytes(0,17)==0,"initial receive has zero completed bytes");
        check(ReceiveProgress.bytes(2,MediaProtocol.CHUNK+17)==MediaProtocol.CHUNK+17,"last partial chunk never overcounts total");
        check(ReceiveProgress.bytes(-1,100)==0,"restored invalid negative progress is clamped");
        check(ReceiveProgress.percent(50,100)==5000,"percentage supports smooth progress");
        check(ReceiveProgress.percent(200,100)==10000,"progress cannot exceed completion");
        check(ReceiveProgress.percent(0,0)==10000,"empty files can complete without division by zero");
        check(ReceiveProgress.detail(50,100).endsWith("50%"),"notification text includes percentage");
        JSONObject sender=new JSONObject().put("pair","a".repeat(32)).put("device","b".repeat(32)).put("peer","c".repeat(32)).put("server","https://example.invalid/").put("token",Protocol.encode(new byte[32])).put("key",Protocol.encode(new byte[32])).put("transport","webdav").put("username","test").put("password","test");
        JSONObject receiver=new JSONObject(sender.toString()).put("device",sender.getString("peer")).put("peer",sender.getString("device"));
        byte[] plain=new byte[2*MediaProtocol.CHUNK+17];new Random(11).nextBytes(plain);String id="d".repeat(32);
        JSONObject offer=new JSONObject().put("id",id).put("sender",sender.getString("device")).put("name","test.bin").put("mime","application/octet-stream").put("kind","file").put("size",plain.length).put("hash","a".repeat(64)).put("chunks",3).put("status","ready").put("expires",System.currentTimeMillis()/1000+3600);
        ByteArrayOutputStream encoded=new ByteArrayOutputStream();for(int part=0;part<3;part++)encoded.write(MediaProtocol.encrypt(sender,id,part,Arrays.copyOfRange(plain,part*MediaProtocol.CHUNK,Math.min(plain.length,(part+1)*MediaProtocol.CHUNK))));
        final byte[][] body={encoded.toByteArray()};HttpServer server=HttpServer.create(new InetSocketAddress("127.0.0.1",0),0);
        server.createContext("/",exchange->{try{exchange.sendResponseHeaders(200,body[0].length);exchange.getResponseBody().write(body[0]);}finally{exchange.close();}});server.start();
        try{MediaWire wire=new MediaWire(receiver);java.lang.reflect.Field origin=MediaWire.class.getDeclaredField("origin");origin.setAccessible(true);origin.set(wire,"http://127.0.0.1:"+server.getAddress().getPort()+"/");
            File target=Path.of(args[0],"received.bin").toFile();List<Long> progress=new ArrayList<>();wire.getBundle(offer,target,progress::add);
            check(Arrays.equals(Files.readAllBytes(target.toPath()),plain),"streamed WebDAV decryption preserves exact file data");
            check(progress.equals(List.of((long)MediaProtocol.CHUNK,2L*MediaProtocol.CHUNK,(long)plain.length)),"WebDAV emits authenticated intermediate and exact final byte progress");
            body[0][MediaProtocol.CHUNK+28]^=1;progress.clear();boolean failed=false;try{wire.getBundle(offer,target,progress::add);}catch(Exception expected){failed=true;}
            check(failed,"corrupt encrypted chunk fails the download");
            check(progress.equals(List.of((long)MediaProtocol.CHUNK)),"invalid chunks never advance progress or report completion");
        }finally{server.stop(0);}
        System.out.println("PASS "+count+" receive progress checks");
    }
}
