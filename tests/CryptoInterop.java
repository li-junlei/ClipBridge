import io.clipbridge.android.Crypto;
import java.util.Base64;
import javax.crypto.Cipher;

public class CryptoInterop {
    public static void main(String[] args) throws Exception {
        Base64.Decoder d=Base64.getDecoder();
        byte[] plain=Crypto.transform(Cipher.DECRYPT_MODE,d.decode(args[0]),d.decode(args[1]),args[2],d.decode(args[3]));
        byte[] encrypted=Crypto.transform(Cipher.ENCRYPT_MODE,d.decode(args[0]),d.decode(args[1]),args[2],plain);
        if(!java.util.Arrays.equals(encrypted,d.decode(args[3]))) throw new AssertionError("Ciphertext mismatch");
        System.out.println(Base64.getEncoder().encodeToString(plain));
    }
}
