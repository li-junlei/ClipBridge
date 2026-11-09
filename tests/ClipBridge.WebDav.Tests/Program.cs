using ClipBridge;
using System.Text.Json;
using System.Security.Cryptography;
void Check(bool value,string name){if(!value)throw new Exception(name);Console.WriteLine("PASS "+name);}
var draft=WebDavPairing.Create(args[0]+"/dav/base/","dav-user","test-only-password");var a=draft.Windows;var b=draft.Android;
using var ca=new RelayClient(a);using var cb=new RelayClient(b);using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));var ct=timeout.Token;
Check(await cb.Receive("",ct) is null,"empty WebDAV inbox");
var e=Wire.Seal(a,1,"WebDAV Unicode 中文 🔐");await ca.Send(e,ct);var got=(await cb.Receive("",ct))!;
Check(Wire.Open(b,got).Text=="WebDAV Unicode 中文 🔐","WebDAV encrypted text delivery in base path");
Check(await cb.Receive(e.Id,ct) is null,"unchanged file is not delivered twice");
var state=new SyncState();Check(state.Accept(b,got)!=null && state.Accept(b,got)==null,"WebDAV replay rejected");
var bytes=Convert.FromBase64String(got.Data);bytes[0]^=1;try{state.Accept(b,got with{Data=Convert.ToBase64String(bytes)});throw new Exception("tamper accepted");}catch(CryptographicException){Check(state.Clock==1,"tampered file cannot advance state");}
await cb.Send(Wire.Seal(b,2,"reply"),ct);Check(Wire.Open(a,(await ca.Receive("",ct))!).Text=="reply","independent reverse mailbox");
await ca.ExchangeName(a,"PC test",ct);Check(await cb.ExchangeName(b,"Phone test",ct)=="PC test","WebDAV encrypted PC name");Check(await ca.ExchangeName(a,"PC test",ct)=="Phone test","WebDAV encrypted phone name");
using(var wrong=new RelayClient(a with{Password="wrong"})){try{await wrong.Send(e,ct);throw new Exception("bad password accepted");}catch(HttpRequestException){Check(true,"wrong WebDAV credentials rejected");}}
using(var redirected=new RelayClient(a with{Server=args[0]+"/dav/redirect/"})){try{await redirected.Send(e,ct);throw new Exception("redirect followed");}catch(HttpRequestException){Check(true,"WebDAV redirects rejected");}}
using(var large=new RelayClient(a with{Server=args[0]+"/dav/oversize/"})){try{await large.Receive("",ct);throw new Exception("oversize accepted");}catch(HttpRequestException){Check(true,"WebDAV oversized response rejected");}}
var qr=WebDavPairing.Qr(b);var encoded=qr.Split('#')[1].Replace('-','+').Replace('_','/');encoded=encoded.PadRight((encoded.Length+3)/4*4,'=');var decoded=JsonSerializer.Deserialize<ClientConfig>(Convert.FromBase64String(encoded),Wire.Json)!;
Check(decoded==b,"offline QR preserves encrypted-sync config locally");
File.WriteAllText(args[1],JsonSerializer.Serialize(new{a,b,envelope=e},Wire.Json));
