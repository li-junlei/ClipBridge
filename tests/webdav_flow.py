"""Local WebDAV fake: credentials, collection creation, cross-client ciphertext and failures."""
from pathlib import Path
import base64,http.server,threading,subprocess,tempfile,json,os,shutil
root=Path(__file__).resolve().parents[1]
files={};collections={'/dav/base/'};trap=[];seen=[];lock=threading.Lock()
auth='Basic '+base64.b64encode(b'dav-user:test-only-password').decode()
class Handler(http.server.BaseHTTPRequestHandler):
 def log_message(self,*args):pass
 def handle_request(self):
  if self.path.startswith('/trap'):trap.append(1)
  if self.headers.get('Authorization')!=auth:self.send_response(401);self.end_headers();return
  if self.path.startswith('/dav/redirect/'):
   self.send_response(302);self.send_header('Location','/trap');self.end_headers();return
  if self.command=='GET' and self.path.startswith('/dav/oversize/'):
   self.send_response(200);self.end_headers()
   try:self.wfile.write(b'x'*600001)
   except (BrokenPipeError,ConnectionResetError):pass
   return
  with lock:
   if self.command=='MKCOL':
    if self.path in collections:code=405
    elif self.path.rstrip('/').rsplit('/',1)[0]+'/' not in collections:code=409
    else:collections.add(self.path);code=201
    self.send_response(code);self.end_headers();return
   if self.command=='PUT':
    body=self.rfile.read(int(self.headers['Content-Length']));seen.append(body)
    if self.path.rsplit('/',1)[0]+'/' not in collections:self.send_response(409);self.end_headers();return
    files[self.path]=body;self.send_response(201);self.end_headers();return
   data=files.get(self.path);self.send_response(200 if data else 404);self.end_headers()
   if data:self.wfile.write(data)
 do_GET=do_PUT=do_MKCOL=handle_request
server=http.server.ThreadingHTTPServer(('127.0.0.1',0),Handler);threading.Thread(target=server.serve_forever,daemon=True).start()
try:
 with tempfile.TemporaryDirectory(prefix='clipbridge-webdav-') as tmp:
  tmp=Path(tmp);fixture=tmp/'fixture.json';origin=f'http://127.0.0.1:{server.server_port}'
  subprocess.run(['dotnet','run','--project',str(root/'tests/ClipBridge.WebDav.Tests'),'--',origin,str(fixture)],check=True)
  f=json.loads(fixture.read_text());c=f['a'];cipher=files['/dav/base/clipbridge/'+c['pair']+'/'+c['device']+'.clip.json'];(tmp/'cipher.json').write_bytes(cipher)
  cache=Path(os.environ.get('GRADLE_USER_HOME',Path.home()/'.gradle'))/'caches/modules-2/files-2.1'
  jars=[]
  for group,artifact,version in [('com.squareup.okhttp3','okhttp','4.12.0'),('com.squareup.okio','okio-jvm',None),('org.jetbrains.kotlin','kotlin-stdlib',None),('org.jetbrains','annotations',None)]:
   folder=cache/group/artifact
   if version:folder=folder/version
   choices=list(folder.rglob('*.jar')) if folder.exists() else []
   if choices:jars.append(str(sorted(choices,key=lambda p:p.stat().st_mtime)[-1]))
  if len(jars)>=3:
   jdk=Path(os.environ['JAVA_HOME'])/'bin' if os.environ.get('JAVA_HOME') else Path(shutil.which('javac')).parent;suffix='.exe' if os.name=='nt' else '';cp=os.pathsep.join(jars)
   subprocess.run([str(jdk/('javac'+suffix)),'-cp',cp,'-d',str(tmp),str(root/'android/app/src/main/java/io/clipbridge/android/WebDav.java'),str(root/'tests/WebDavInterop.java')],check=True)
   subprocess.run([str(jdk/('java'+suffix)),'-cp',str(tmp)+os.pathsep+cp,'io.clipbridge.android.WebDavInterop',origin,c['pair'],c['device'],str(tmp/'cipher.json')],check=True,timeout=40)
  else:print('SKIP Android JVM transport check: build Android first to resolve OkHttp dependencies')
  for body in seen:
   assert all(value.encode() not in body for value in [c['key'],c['password'],'WebDAV Unicode','PC test','Phone test'])
   assert 'data' in json.loads(body) and 'nonce' in json.loads(body)
  assert not trap,'redirect leaked credentials'
  print('PASS WebDAV stores only encrypted envelopes; redirects never reached target')
finally:server.shutdown();server.server_close()
