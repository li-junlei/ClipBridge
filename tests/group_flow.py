from pathlib import Path
import subprocess,tempfile,socket,os,time,secrets,base64,urllib.request,json
r=Path(__file__).resolve().parents[1]
with tempfile.TemporaryDirectory(prefix='clipbridge-groups-') as temp:
 temp=Path(temp)
 with socket.socket() as sock:sock.bind(('127.0.0.1',0));port=sock.getsockname()[1]
 origin=f'http://127.0.0.1:{port}';setup=base64.b64encode(secrets.token_bytes(32)).decode();env=dict(os.environ,ASPNETCORE_URLS=origin,CLIPBRIDGE_RELAY_CONFIG=str(temp/'missing.json'),CLIPBRIDGE_DATA=str(temp/'server'),CLIPBRIDGE_SETUP_CODE=setup)
 subprocess.run(['dotnet','build',str(r/'src/ClipBridge.Relay'),'-c','Release'],check=True,stdout=subprocess.DEVNULL)
 with (temp/'server.log').open('w') as log:
  process=subprocess.Popen(['dotnet',str(r/'src/ClipBridge.Relay/bin/Release/net10.0/ClipBridge.Relay.dll')],env=env,stdout=log,stderr=log)
  try:
   opener=urllib.request.build_opener(urllib.request.ProxyHandler({}))
   for i in range(60):
    try:
     with opener.open(origin+'/health',timeout=1) as response:assert json.load(response)['status']=='ok'
     break
    except Exception:time.sleep(.2)
   subprocess.run(['dotnet','run','--project',str(r/'tests/ClipBridge.Group.Tests'),'--',origin,setup,str(temp/'client')],check=True,timeout=180)
   process.terminate();process.wait(timeout=10)
   with socket.socket() as sock:sock.bind(('127.0.0.1',0));port=sock.getsockname()[1]
   origin=f'http://127.0.0.1:{port}';env['ASPNETCORE_URLS']=origin
   fixture=temp/'client/identity.json';f=json.loads(fixture.read_text());f['groupC']['connection']['server']=origin;f['groupB']['connection']['server']=origin;fixture.write_text(json.dumps(f))
   process=subprocess.Popen(['dotnet',str(r/'src/ClipBridge.Relay/bin/Release/net10.0/ClipBridge.Relay.dll')],env=env,stdout=log,stderr=log)
   for i in range(60):
    try:
     with opener.open(origin+'/health',timeout=1) as response:assert json.load(response)['status']=='ok'
     break
    except Exception:time.sleep(.2)
   import urllib.error
   def request(method,path,authorization=None,body=None):
    headers={}
    if authorization:headers['Authorization']=authorization
    if isinstance(body,dict):headers['Content-Type']='application/json';body=json.dumps(body).encode()
    try:
     with opener.open(urllib.request.Request(origin+path,data=body,headers=headers,method=method),timeout=10) as response:return response.status,response.read()
    except urllib.error.HTTPError as e:return e.code,e.read()
   def check(value,label):assert value,label;print('PASS '+label,flush=True)
   g=f['groupC'];gid=g['id'];basic='Basic '+base64.b64encode(('group:'+g['access']).encode()).decode();base='/v2/groups/'+gid+'/store/'
   check(request('GET',base+'members/'+f['c']['member']['id']+'.json')[0]==401,'group storage rejects anonymous access')
   check(request('GET',base+'members/'+f['c']['member']['id']+'.json','Basic '+base64.b64encode(b'group:wrong').decode())[0]==401,'group storage rejects wrong access credential')
   check(request('GET',base+'members/'+f['c']['member']['id']+'.json',basic)[0]==404,'member feed is not fabricated before its first publication')
   check(request('POST','/v2/groups','Bearer wrong',{'id':secrets.token_hex(16),'authHash':base64.b64encode(secrets.token_bytes(32)).decode()})[0]==401,'group creation requires server management authorization')
   locator='ABCDEFGHJKLMNPQRSTUVWXYZ23456789'[:6];owner=base64.b64encode(secrets.token_bytes(32)).decode();guest=base64.b64encode(secrets.token_bytes(32)).decode();other=base64.b64encode(secrets.token_bytes(32)).decode()
   registration={'meta':{'group':gid,'root':'0'*64,'expires':int(time.time())+300,'first':f['ar1']},'ownerHash':base64.b64encode(__import__('hashlib').sha256(owner.encode()).digest()).decode()}
   path='/v2/groups/'+gid+'/invitations/'+locator;slots='/v2/invitations/'+locator+'/'
   check(request('POST',path,'Bearer wrong',registration)[0]==401,'only a group credential can register an invitation')
   check(request('POST',path,'Bearer '+g['access'],registration)[0]==200,'authorized invitation is created')
   status,public=request('GET',slots+'meta.json');check(status==200 and g['access'].encode() not in public and g['directoryKey'].encode() not in public,'public rendezvous exposes neither group access nor encryption key')
   check(request('PUT',slots+'a2.json','Bearer '+guest,{'test':1})[0]==401,'guest cannot write inviter handshake slot')
   check(request('PUT',slots+'b1.json','Bearer '+guest,f['ar1'])[0]==200,'first guest claims the invitation')
   check(request('PUT',slots+'b1.json','Bearer '+other,f['ar1'])[0]==401,'second guest cannot replace the claimed invitation')
   check(request('PUT',slots+'b2.json','Bearer '+owner,{'test':1})[0]==401,'inviter cannot impersonate the claimed guest')
   check(request('PUT',slots+'a2.json','Bearer '+owner,{'test':1})[0]==200 and request('PUT',slots+'a2.json','Bearer '+owner,{'test':2})[0]==412,'handshake slots cannot be overwritten')
   import http.client
   oversized=http.client.HTTPConnection('127.0.0.1',port,timeout=10);oversized.putrequest('PUT',base+'oversize.json');oversized.putheader('Authorization',basic);oversized.putheader('Content-Length','600001');oversized.endheaders();check(oversized.getresponse().status==413,'group JSON body bound is enforced');oversized.close()
   check(request('PUT',base+'InvalidName.json',basic,b'{}')[0]==400,'unsafe group object names are rejected')
   registration['meta']['expires']=int(time.time())-1
   check(request('POST',path+'Z','Bearer '+g['access'],registration)[0]==400,'invalid invitation identifiers are rejected')
   check(request('POST','/v2/groups/'+gid+'/invitations/BCDEFG','Bearer '+g['access'],registration)[0]==400,'expired invitation registration is rejected')
   jar=r/'artifacts/test-deps/json-20250517.jar';jar.parent.mkdir(parents=True,exist_ok=True)
   if not jar.exists():urllib.request.urlretrieve('https://repo.maven.apache.org/maven2/org/json/json/20250517/json-20250517.jar',jar)
   cache=Path.home()/'.gradle/caches/modules-2/files-2.1';jars=[str(jar)]
   for group,artifact in [('org.bouncycastle','bcprov-jdk18on'),('com.squareup.okhttp3','okhttp'),('com.squareup.okio','okio-jvm'),('org.jetbrains.kotlin','kotlin-stdlib'),('org.jetbrains','annotations')]:
    choices=list((cache/group/artifact).rglob('*.jar'));assert choices; j=sorted(choices,key=lambda p:p.stat().st_mtime)[-1];jars.append(str(j))
   jdk=Path(r'C:/Program Files/Android/Android Studio/jbr/bin');cp=os.pathsep.join(jars);java=r/'android/app/src/main/java/io/clipbridge/android';classes=temp/'classes';classes.mkdir()
   names=['Crypto.java','Protocol.java','MediaProtocol.java','GroupCrypto.java','GroupPake.java','GroupNetwork.java','GroupSession.java']
   subprocess.run([str(jdk/'javac.exe'),'-encoding','UTF-8','-cp',cp,'-d',str(classes),*[str(java/n) for n in names],str(r/'tests/GroupInterop.java')],check=True)
   host=subprocess.Popen(['dotnet','run','--project',str(r/'tests/ClipBridge.Group.Tests'),'--',origin,setup,str(temp/'client'),'interop'])
   try:
    subprocess.run([str(jdk/'java.exe'),'-cp',str(classes)+os.pathsep+cp,'io.clipbridge.android.GroupInterop',str(temp/'client')],check=True,timeout=150)
    assert host.wait(timeout=30)==0
   finally:
    if host.poll() is None:host.terminate();host.wait(timeout=10)
   for mode in ['windows','phones']:
    process.terminate();process.wait(timeout=10)
    with socket.socket() as sock:sock.bind(('127.0.0.1',0));port=sock.getsockname()[1]
    origin=f'http://127.0.0.1:{port}';env['ASPNETCORE_URLS']=origin
    process=subprocess.Popen(['dotnet',str(r/'src/ClipBridge.Relay/bin/Release/net10.0/ClipBridge.Relay.dll')],env=env,stdout=log,stderr=log)
    for i in range(60):
     try:
      with opener.open(origin+'/health',timeout=1) as response:assert json.load(response)['status']=='ok'
      break
     except Exception:time.sleep(.2)
    case=temp/mode;case.mkdir();(case/'java-input.json').write_text(json.dumps({'connection':{'server':origin,'transport':'relay'},'setup':setup}))
    guest=None
    if mode=='windows':guest=subprocess.Popen(['dotnet','run','--project',str(r/'tests/ClipBridge.Group.Tests'),'--',origin,setup,str(case),'guest'])
    try:
     subprocess.run([str(jdk/'java.exe'),'-cp',str(classes)+os.pathsep+cp,'io.clipbridge.android.GroupInterop',str(case),mode],check=True,timeout=150)
     if guest:assert guest.wait(timeout=20)==0
    finally:
     if guest and guest.poll() is None:guest.terminate();guest.wait(timeout=10)


  finally:process.terminate();process.wait(timeout=10)

import http.server,threading
code=(r/'tests/webdav_flow.py').read_text();ns={'__name__':'group_dav_fake','__file__':str(r/'tests/webdav_flow.py')};exec(code[:code.index('server=http.server.ThreadingHTTPServer')],ns)
class Dav(ns['Handler']):
 def do_MKCOL(self):
  if '/clipbridge/invites/' in self.path and self.path in ns['collections']:
   self.send_response(400);self.end_headers();return
  super().handle_request()
 def do_PROPFIND(self):
  if self.headers.get('Authorization')!=ns['auth']:self.send_response(401)
  else:self.send_response(207 if self.path in ns['collections'] else 404)
  self.end_headers()
 def do_PUT(self):
  if self.headers.get('If-None-Match')=='*' and self.path in ns['files']:
   self.rfile.read(int(self.headers.get('Content-Length','0')));self.send_response(412);self.end_headers();return
  super().handle_request()
server=http.server.ThreadingHTTPServer(('127.0.0.1',0),Dav);threading.Thread(target=server.serve_forever,daemon=True).start()
try:
 with tempfile.TemporaryDirectory(prefix='clipbridge-group-dav-') as tmp:
  origin=f'http://127.0.0.1:{server.server_port}/dav/base/'
  subprocess.run(['dotnet','run','--project',str(r/'tests/ClipBridge.Group.Tests'),'--',origin,'unused',tmp,'webdav'],check=True,timeout=180)
  assert any(path.endswith('.cbx') for path in ns['files'])
  for body in ns['seen']:assert b'Computer A' not in body and b'Phone B' not in body
  print('PASS WebDAV group rendezvous, membership, encrypted text and binary object exchange')
finally:server.shutdown();server.server_close()
