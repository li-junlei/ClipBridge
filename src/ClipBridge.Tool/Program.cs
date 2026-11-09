using ClipBridge;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

if (args.Length != 3 || args[0] != "pair")
{ Console.Error.WriteLine("Usage: ClipBridge.Tool pair https://relay.example.com NEW_PRIVATE_DIRECTORY"); return 1; }
string pair = Guid.NewGuid().ToString("N"), a = Guid.NewGuid().ToString("N"), b = Guid.NewGuid().ToString("N");
string key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
string ta = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), tb = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
var ca = new ClientConfig(args[1], pair, a, b, ta, key); ca.Validate();
var cb = new ClientConfig(args[1], pair, b, a, tb, key); cb.Validate();
if (Directory.Exists(args[2])) throw new IOException("Destination already exists; refusing to overwrite pairing.");
Directory.CreateDirectory(args[2]);
void Save(string name, object value) => File.WriteAllText(Path.Combine(args[2], name), JsonSerializer.Serialize(value, Wire.Json));
Save("windows.json", ca); Save("android.json", cb);
Save("relay.json", new { pair, devices = new[] {
    new { id = a, tokenHash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(ta))) },
    new { id = b, tokenHash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(tb))) }
} });
Console.WriteLine("Created local pairing files. Only relay.json belongs on the server. Import client files locally, then remove plaintext copies.");
return 0;
