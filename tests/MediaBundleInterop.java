package io.clipbridge.android;
import java.nio.file.*;
import java.security.*;
import org.json.*;
public final class MediaBundleInterop {
    public static void main(String[] args)throws Exception{
        Path fixture=Path.of(args[0]);JSONObject f=new JSONObject(Files.readString(fixture));JSONObject offer=f.getJSONObject("offer");JSONObject c=new JSONObject(f.getJSONObject("receiver").toString());String local=c.getString("server");if(!local.startsWith("http://127.0.0.1:"))throw new AssertionError("Local fixture required");c.put("server",local.replace("http://","https://"));MediaWire receiver=new MediaWire(c);java.lang.reflect.Field origin=MediaWire.class.getDeclaredField("origin");origin.setAccessible(true);origin.set(receiver,local);Path received=fixture.resolveSibling("java-bulk.bin");receiver.getBundle(offer,received.toFile());if(!MessageDigest.isEqual(Files.readAllBytes(received),Files.readAllBytes(Path.of(f.getString("source")))))throw new AssertionError("Bundle mismatch");
        receiver.putBundle(received.toFile(),offer);System.out.println("PASS Android downloads/decrypts and uploads one WebDAV encrypted object");
    }
}
