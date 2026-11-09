package io.clipbridge.android;

import javax.crypto.Cipher;
import javax.crypto.Mac;
import javax.crypto.spec.GCMParameterSpec;
import javax.crypto.spec.SecretKeySpec;
import java.nio.charset.StandardCharsets;
import java.util.Arrays;

public final class Crypto {
    public static byte[] transform(int mode, byte[] root, byte[] nonce, String header, byte[] data) throws Exception {
        byte[] aad = header.getBytes(StandardCharsets.US_ASCII);
        Mac mac = Mac.getInstance("HmacSHA256"); mac.init(new SecretKeySpec(root, "HmacSHA256"));
        byte[] key = mac.doFinal(aad);
        try {
            Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding");
            cipher.init(mode, new SecretKeySpec(key, "AES"), new GCMParameterSpec(128, nonce));
            cipher.updateAAD(aad); return cipher.doFinal(data);
        } finally { Arrays.fill(key, (byte)0); }
    }
}
