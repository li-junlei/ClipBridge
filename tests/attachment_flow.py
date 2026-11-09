"""Isolated file-transfer consent, fallback, tamper and Java/.NET crypto checks."""
from pathlib import Path
import importlib.util,tempfile,subprocess,threading,json,os,urllib.request,base64
root=Path(__file__).resolve().parents[1]
# Reuse the local DAV handler without executing its CLI checks.
code=(root/'tests/webdav_flow.py').read_text();namespace={'__name__':'attachment_fake','__file__':str(root/'tests/webdav_flow.py')};exec(code[:code.index('server=http.server.ThreadingHTTPServer')],namespace)
server=namespace['http'].server.ThreadingHTTPServer(('127.0.0.1',0),namespace['Handler']);threading.Thread(target=server.serve_forever,daemon=True).start()
try:
 with tempfile.TemporaryDirectory(prefix='clipbridge-attachments-') as tmp:
  tmp=Path(tmp);origin=f'http://127.0.0.1:{server.server_port}'
  subprocess.run(['dotnet','run','--project',str(root/'tests/ClipBridge.Attachment.Tests'),'--',origin,str(tmp)],check=True,timeout=120)
  fixture=json.loads((tmp/'crypto.json').read_text());key=fixture['sender']['key']
  idle=(tmp/'idle-pair.txt').read_text();requests=[(m,p) for m,p in namespace['requests'] if '/'+idle+'/' in p];assert sum(m=='GET' for m,p in requests)==1 and sum(m=='PUT' for m,p in requests)==1
  print('PASS 40 idle ticks issue only one WebDAV manifest read and one initial write')

  for body in namespace['seen']:
   assert key.encode() not in body and b'example.bin' not in body
  print('PASS WebDAV attachment storage contains ciphertext only')
  jar=root/'artifacts/test-deps/json-20250517.jar';jar.parent.mkdir(parents=True,exist_ok=True)
  if not jar.exists():urllib.request.urlretrieve('https://repo.maven.apache.org/maven2/org/json/json/20250517/json-20250517.jar',jar)
  jdk=Path(os.environ['JAVA_HOME'])/'bin';suffix='.exe' if os.name=='nt' else ''
  java=root/'android/app/src/main/java/io/clipbridge/android'
  subprocess.run([str(jdk/('javac'+suffix)),'-encoding','UTF-8','-cp',str(jar),'-d',str(tmp),str(java/'Crypto.java'),str(java/'Protocol.java'),str(java/'MediaProtocol.java'),str(root/'tests/AttachmentInterop.java')],check=True)
  subprocess.run([str(jdk/('java'+suffix)),'-cp',str(tmp)+os.pathsep+str(jar),'io.clipbridge.android.AttachmentInterop',str(tmp/'crypto.json'),str(tmp/'response.bin')],check=True)
  from cryptography.hazmat.primitives.ciphers.aead import AESGCM
  import hmac,hashlib
  c=fixture['sender'];aad=f"clipbridge:attachment:1:{c['pair']}:{c['device']}:{c['peer']}:{fixture['id']}:0".encode();derived=hmac.new(base64.b64decode(c['key']),aad,hashlib.sha256).digest();blob=(tmp/'response.bin').read_bytes();assert AESGCM(derived).decrypt(blob[:12],blob[12:],aad).decode()=='interop attachment 中文'
  print('PASS independent AES-GCM check on Android encrypted attachment')
  cache=Path(os.environ.get('GRADLE_USER_HOME',Path.home()/'.gradle'))/'caches/modules-2/files-2.1';jars=[str(jar)]
  for group,artifact in [('com.squareup.okhttp3','okhttp'),('com.squareup.okio','okio-jvm'),('org.jetbrains.kotlin','kotlin-stdlib'),('org.jetbrains','annotations')]:
   choices=list((cache/group/artifact).rglob('*.jar'));assert choices,'Build Android first';jars.append(str(sorted(choices,key=lambda p:p.stat().st_mtime)[-1]))
  cp=os.pathsep.join(jars)
  subprocess.run([str(jdk/('javac'+suffix)),'-encoding','UTF-8','-cp',cp,'-d',str(tmp),str(java/'Crypto.java'),str(java/'Protocol.java'),str(java/'MediaProtocol.java'),str(java/'MediaWire.java'),str(root/'tests/MediaBundleInterop.java')],check=True)
  subprocess.run([str(jdk/('java'+suffix)),'-cp',str(tmp)+os.pathsep+cp,'io.clipbridge.android.MediaBundleInterop',str(tmp/'bulk.json')],check=True,timeout=60)
  f=json.loads((tmp/'bulk.json').read_text());c=f['receiver'];o=f['offer'];blob=namespace['files']['/dav/base/clipbridge/'+c['pair']+'/attachments/'+c['device']+'/'+o['id']+'.cbx'];plain=bytearray();offset=0
  for part in range(o['chunks']):
   length=min(262144,o['size']-part*262144)+28;block=blob[offset:offset+length];offset+=length;aad=f"clipbridge:attachment:1:{c['pair']}:{c['device']}:{c['peer']}:{o['id']}:{part}".encode();key=hmac.new(base64.b64decode(c['key']),aad,hashlib.sha256).digest();plain.extend(AESGCM(key).decrypt(block[:12],block[12:],aad))
  assert offset==len(blob) and plain==Path(f['source']).read_bytes()
  print('PASS independently decrypt complete Android WebDAV bundle')

finally:server.shutdown();server.server_close()
