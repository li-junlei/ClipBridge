package io.clipbridge.android;
import java.util.Locale;
final class ReceiveProgress {
    static long bytes(long parts,long size){return Math.max(0,Math.min(size,Math.max(0,Math.min(parts,400))*MediaProtocol.CHUNK));}
    static int percent(long bytes,long size){return size==0?10000:size<0?0:(int)(Math.max(0,Math.min(bytes,size))*10000/size);}
    static String detail(long bytes,long size){return String.format(Locale.ROOT,"%.2f / %.2f MiB · %d%%",Math.max(0,Math.min(bytes,size))/1048576d,size/1048576d,percent(bytes,size)/100);}
}
