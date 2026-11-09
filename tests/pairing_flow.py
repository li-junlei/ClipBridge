"""Focused provisioning/one-time encrypted invite check on an isolated loopback relay."""
import base64,json,os,socket,subprocess,tempfile,time,urllib.request,urllib.error
from pathlib import Path
from urllib.parse import urlparse,parse_qs
from cryptography.hazmat.primitives.ciphers.aead import AESGCM
root=Path(__file__).resolve().parents[1]
with tempfile.TemporaryDirectory(prefix='clipbridge-pairing-') as temp:
 temp=Path(temp);fixture=temp/'fixture.json'
 subprocess.run(['dotnet','run','--project',str(root/'tests/ClipBridge.Tests'),'--',str(fixture)],check=True,stdout=subprocess.DEVNULL)
 p=json.loads(fixture.read_text(encoding='utf-8'))['pairing'];d=p['draft'];invite=p['invite']['envelope']
 with socket.socket() as sock:sock.bind(('127.0.0.1',0));port=sock.getsockname()[1]
 setup=base64.b64encode(os.urandom(32)).decode();origin=f'http://127.0.0.1:{port}'
 env=dict(os.environ,ASPNETCORE_URLS=origin,CLIPBRIDGE_RELAY_CONFIG=str(temp/'no-legacy.json'),CLIPBRIDGE_DATA=str(temp/'data'),CLIPBRIDGE_SETUP_CODE=setup)
 log=open(temp/'relay.log','w');process=subprocess.Popen(['dotnet',str(root/'src/ClipBridge.Relay/bin/Release/net10.0/ClipBridge.Relay.dll')],env=env,stdout=log,stderr=log)
 opener=urllib.request.build_opener(urllib.request.ProxyHandler({}))
 def call(path,token=None,body=None):
  headers={'Content-Type':'application/json'}
  if token:headers['Authorization']='Bearer '+token
  request=urllib.request.Request(origin+path,data=None if body is None else json.dumps(body).encode(),headers=headers)
  try:
   with opener.open(request,timeout=10) as r:
    b=r.read();return r.status,json.loads(b) if b else None
  except urllib.error.HTTPError as e:return e.code,None
 def check(condition,name):
  assert condition,name;print('PASS '+name,flush=True)
 try:
  for _ in range(100):
   try:
    if call('/health')[0]==200:break
   except OSError:pass
   time.sleep(.1)
  check(call('/v1/setup/info')[1]['requiresSetupCode'],'fresh server starts without manual relay.json')
  check(call('/v1/pairings','wrong',p['registration'])[0]==401,'provisioning requires setup code')
  check(call('/v1/pairings',setup,p['registration'])[0]==200,'desktop registers authentication digests and encrypted invite')
  check(call('/v1/inbox?after=invalid',d['windows']['token'])[0]==400,'registered desktop token is live immediately')
  status,encrypted=call('/v1/pairing-invites/claim',body={'ticket':invite['id']})
  check(status==200 and encrypted==invite,'phone can claim encrypted invite')
  qr=urlparse(p['invite']['qr']);key=base64.urlsafe_b64decode(qr.fragment+'=')
  plain=AESGCM(key).decrypt(base64.b64decode(invite['nonce']),base64.b64decode(invite['data']),f"clipbridge-pair:1:{invite['id']}:{invite['expires']}".encode())
  check(json.loads(plain)==d['android'],'QR-only transfer key decrypts phone config locally')
  check(call('/v1/pairing-invites/claim',body={'ticket':invite['id']})[0]==410,'invite cannot be claimed twice')
  check(call('/v1/messages',d['windows']['token'],p['message'])[0]==200 and call('/v1/inbox',d['android']['token'])[1]==p['message'],'new pair can relay encrypted clipboard')
  check(call('/v1/device-info',body=p['message'])[0]==401,'metadata rejects unauthenticated write')
  check(call('/v1/device-info',d['android']['token'],p['message'])[0]==403,'metadata rejects forged sender')
  check(call('/v1/device-info',d['windows']['token'],p['message'])[0]==200 and call('/v1/device-info',d['android']['token'])[1]==p['message'],'independent authenticated metadata mailbox')
  check(call('/v1/inbox',d['android']['token'])[1]==p['message'],'metadata leaves clipboard inbox unchanged')
  stored=(temp/'data/pairings.state').read_text()
  check(d['windows']['key'] not in stored and d['android']['token'] not in stored and invite['data'] not in stored,'relay stores no plaintext content key and consumes invite ciphertext')
 finally:
  process.terminate();process.wait(timeout=10);log.close()
