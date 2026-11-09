using ClipBridge;
using System.Security.Cryptography;
using System.Text.Json;

int passed = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); passed++; Console.WriteLine("PASS " + name); }
void Reject(Action action, string name)
{ try { action(); } catch (Exception ex) when (ex is CryptographicException or ArgumentException or FormatException) { Check(true, name); return; } throw new Exception("Accepted: " + name); }
var a = new ClientConfig("http://localhost:8787", new string('a',32), new string('b',32), new string('c',32), Convert.ToBase64String(new byte[32]), Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
var b = a with { Device = a.Peer, Peer = a.Device };
var identity=DeviceIdentity.Seal(a,"DESKTOP-中文");
Check(DeviceIdentity.Open(b,identity)=="DESKTOP-中文","encrypted device name roundtrip");
Reject(()=>Wire.Open(b,identity),"device metadata cannot become clipboard text");
Reject(()=>DeviceIdentity.Open(a,identity),"metadata rejects wrong recipient");
Reject(()=>DeviceIdentity.Seal(a,"bad\nname"),"metadata rejects control characters");
long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
var e = Wire.Seal(a, 1, "中文 🐱\r\nclipboard", now);
Check(Wire.Open(b, e, now).Text == "中文 🐱\r\nclipboard", "Unicode roundtrip");
var boundary = Wire.Seal(a, 2, new string('中', 21845), now); Wire.Validate(boundary);
Check(Wire.Open(b, boundary, now).Text.Length == 21845, "64 KiB Unicode boundary survives JSON escaping");
var controls = Wire.Seal(a, 3, new string('\u0001', 65536), now); Wire.Validate(controls);
Check(Wire.Open(b, controls, now).Text.Length == 65536, "64 KiB control characters fit envelope limit");
Reject(() => Wire.Open(b, e with { Clock = 2 }, now), "authenticated clock");
Reject(() => Wire.Open(b, e with { Id = new string('d',32) }, now), "authenticated message ID");
Reject(() => Wire.Open(b with { Key = Convert.ToBase64String(new byte[32]) }, e, now), "wrong key");
Reject(() => Wire.Open(a, e, now), "wrong recipient");
var bytes = Convert.FromBase64String(e.Data); bytes[0] ^= 1;
Reject(() => Wire.Open(b, e with { Data = Convert.ToBase64String(bytes) }, now), "tampered ciphertext");
Reject(() => Wire.Open(b, e, now + 600), "expired message");
Reject(() => Wire.Seal(a, 2, new string('中', Wire.MaxText)), "byte-size limit");
Reject(() => (a with { Server = "http://example.com" }).Validate(), "reject remote plaintext transport");
var state = new SyncState();
state.Validate(b);
Reject(() => new SyncState { Clock = -1 }.Validate(b), "negative replay state rejected");
Reject(() => new SyncState { Clock = 1, Winner = new string('d',32) }.Validate(b), "unknown replay winner rejected");
Reject(() => new SyncState { Clock = 0, Winner = b.Device }.Validate(b), "invalid initial replay state rejected");
Reject(() => new SyncState { Clock = 2, Winner = a.Device, Pending = e }.Validate(a), "mismatched pending replay state rejected");
Check(state.Accept(b, e) is not null, "first delivery");
Check(state.Accept(b, e) is null, "reject replay");
var saved = JsonSerializer.Serialize(state, Wire.Json);
state = JsonSerializer.Deserialize<SyncState>(saved, Wire.Json)!;
Check(state.Accept(b, e) is null, "replay rejected after reload");
state.Queue(b, "new local");
Check(state.Accept(b, e) is null && state.Pending is not null, "old remote cannot overwrite local");
var concurrentA = new SyncState(); var concurrentB = new SyncState();
var ea = concurrentA.Queue(a, "a"); var eb = concurrentB.Queue(b, "b");
concurrentA.Accept(a, eb); concurrentB.Accept(b, ea);
Check(concurrentA.Clock == concurrentB.Clock && concurrentA.Winner == concurrentB.Winner, "concurrent copies converge");
var draft = PairingDraft.Create("http://localhost:8787"); var invite = draft.Invite();
var registration = new PairingRegistration(draft.Relay, draft.Windows.Device, invite.Envelope);
Check(!JsonSerializer.Serialize(registration, Wire.Json).Contains(draft.Windows.Key, StringComparison.Ordinal), "pairing registration never contains plaintext content key");
var vector = new { config = a, envelope = e, nextEnvelope = Wire.Seal(a, 4, "next", now), controls, text = "中文 🐱\r\nclipboard", now,
    pairing = new { draft, invite, registration, message = Wire.Seal(draft.Windows, 1, "pairing smoke", now) } };
if (args.Length == 1) File.WriteAllText(args[0], JsonSerializer.Serialize(vector, Wire.Json));
Console.WriteLine($"{passed} checks passed.");
