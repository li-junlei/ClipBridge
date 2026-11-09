package io.clipbridge.android;
import java.nio.file.*;
public final class WebDavInterop {
 public static void main(String[] args)throws Exception{
  WebDav dav=new WebDav(args[0]+"/dav/base/","dav-user","test-only-password",args[1]);
  String body=dav.get(args[2]+".clip.json");if(!body.equals(Files.readString(Path.of(args[3]))))throw new AssertionError("cipher mismatch");dav.put(args[2]+".clip.json",body);
  if(!dav.get("missing.json").equals("null"))throw new AssertionError();
  try{new WebDav(args[0]+"/dav/redirect/","dav-user","test-only-password",args[1]).put("test.json",body);throw new AssertionError("redirect");}catch(java.io.IOException expected){}
  try{new WebDav(args[0]+"/dav/oversize/","dav-user","test-only-password",args[1]).get("large.json");throw new AssertionError("size");}catch(java.io.IOException expected){}
  System.out.println("PASS Android WebDAV GET/PUT/MKCOL, 404, redirect and size limits");System.exit(0);
 }
}
