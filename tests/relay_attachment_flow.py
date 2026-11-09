"""Real relay attachment API: scope, owner-only writes/deletes, bounds, restart persistence."""
from pathlib import Path
import subprocess,tempfile,socket,os,json,urllib.request,urllib.error,time,secrets,base64,hashlib
root=Path(__file__).resolve().parents[1]
with tempfile.TemporaryDirectory(prefix='clipbridge-relay-objects-') as directory:
 directory=Path(directory);fixture=directory/'fixture.json';subprocess.run(['dotnet','run','--project',str(root/'tests/ClipBridge.Tests'),'--',str(fixture)],check=True,stdout=subprocess.DEVNULL);f=json.loads(fixture.read_text());p=f['pairing'];a=p['draft']['windows'];b=p['draft']['android']
 with socket.socket() as s:s.bind(('127.0.0.1',0));port=s.getsockname()[1]
 setup=base64.b64encode(secrets.token_bytes(32)).decode();origin=f'http://127.0.0.1:{port}';env=dict(os.environ,ASPNETCORE_URLS=origin,CLIPBRIDGE_RELAY_CONFIG=str(directory/'missing.json'),CLIPBRIDGE_DATA=str(directory/'data'),CLIPBRIDGE_SETUP_CODE=setup);opener=urllib.request.build_opener(urllib.request.ProxyHandler({}))
 def request(method,path,token=None,body=None):
  headers={}
  if token:headers['Authorization']='Bearer '+token
  if isinstance(body,dict):headers['Content-Type']='application/json';body=json.dumps(body).encode()
  try:
   with opener.open(urllib.request.Request(origin+path,data=body,headers=headers,method=method),timeout=10) as response:return response.status,response.read()
  except urllib.error.HTTPError as e:return e.code,e.read()
 def start():
  log=open(directory/'server.log','w');proc=subprocess.Popen(['dotnet',str(root/'src/ClipBridge.Relay/bin/Release/net10.0/ClipBridge.Relay.dll')],env=env,stdout=log,stderr=log)
  for _ in range(100):
   try:
    if request('GET','/health')[0]==200:return proc,log
   except OSError:pass
   time.sleep(.1)
  proc.terminate();raise AssertionError('relay startup')
 def check(ok,name):assert ok,name;print('PASS '+name,flush=True)
 proc,log=start()
 try:
  check(json.loads(request('GET','/v1/setup/info')[1])['attachments']==1,'attachment capability advertised')
  check(request('POST','/v1/pairings',setup,p['registration'])[0]==200,'test pair registration')
  id=secrets.token_hex(16);path='/v1/objects/'+a['device']+'/'+id+'-0';blob=secrets.token_bytes(100)
  check(request('PUT',path,a['token'],blob)[0]==200,'sender uploads bounded ciphertext chunk')
  check(request('GET',path,b['token'])==(200,blob),'paired recipient reads exact ciphertext')
  check(request('PUT',path,b['token'],blob)[0]==403,'recipient cannot overwrite sender chunks')
  check(request('DELETE',path,b['token'])[0]==403,'recipient cannot delete sender chunks')
  check(request('PUT','/v1/objects/'+a['device']+'/'+id+'-400',a['token'],blob)[0]==403,'out-of-range chunk index rejected')
  check(request('PUT',path,a['token'],bytes(256*1024+29))[0]==413,'oversized chunk rejected')
  check(request('PUT','/v1/objects/'+a['device']+'/index',a['token'],b'{}')[0]==400,'malformed encrypted manifest rejected')
  check(request('PUT','/v1/objects/'+a['device']+'/index',a['token'],p['message'])[0]==200,'authenticated opaque manifest persisted')
  other=secrets.token_hex(16);x=secrets.token_hex(16);y=secrets.token_hex(16);token=base64.b64encode(secrets.token_bytes(32)).decode();t2=base64.b64encode(secrets.token_bytes(32)).decode();registration={'relay':{'pair':other,'devices':[{'id':x,'tokenHash':base64.b64encode(hashlib.sha256(token.encode()).digest()).decode()},{'id':y,'tokenHash':base64.b64encode(hashlib.sha256(t2.encode()).digest()).decode()}]},'owner':x,'invite':{'id':secrets.token_hex(16),'expires':int(time.time())+300,'nonce':base64.b64encode(secrets.token_bytes(12)).decode(),'data':base64.b64encode(secrets.token_bytes(32)).decode()}}
  assert request('POST','/v1/pairings',setup,registration)[0]==200
  check(request('GET',path,token)[0]==403,'another pair cannot read attachment objects')
  proc.terminate();proc.wait(timeout=10);log.close();proc,log=start();check(request('GET',path,b['token'])==(200,blob),'ciphertext survives relay restart')
  check(request('DELETE',path,a['token'])[0]==200 and request('GET',path,b['token'])[0]==404,'sender cleans completed ciphertext')
  check(all(a['key'].encode() not in f.read_bytes() for f in (directory/'data').rglob('*.blob')),'relay objects contain no plaintext content key')
 finally:proc.terminate();proc.wait(timeout=10);log.close()
