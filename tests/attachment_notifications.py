"""Android attachment idle/expiry/transport regression tests, run on the host JVM."""
from pathlib import Path
import os, subprocess, tempfile
root = Path(__file__).resolve().parents[1]
jdk = Path(os.environ.get('JAVA_HOME', r'C:/Program Files/Android/Android Studio/jbr')) / 'bin'
cache = Path(os.environ.get('GRADLE_USER_HOME', Path.home()/'.gradle'))/'caches/modules-2/files-2.1'
jars = [root/'artifacts/test-deps/json-20250517.jar']
for group, artifact in [('com.squareup.okhttp3','okhttp'), ('com.squareup.okio','okio-jvm'), ('org.jetbrains.kotlin','kotlin-stdlib'), ('org.jetbrains','annotations')]:
    choices = list((cache/group/artifact).rglob('*.jar'))
    if not choices: raise RuntimeError('Build Android first: missing '+artifact)
    jars.append(max(choices, key=lambda p:p.stat().st_mtime))
cp = os.pathsep.join(map(str, jars))
java = root/'android/app/src/main/java/io/clipbridge/android'
base = root/'work/test-tmp'; base.mkdir(parents=True, exist_ok=True)
with tempfile.TemporaryDirectory(prefix='attachment-notifications-', dir=base) as tmp:
    suffix = '.exe' if os.name=='nt' else ''
    subprocess.run([str(jdk/('javac'+suffix)), '-encoding','UTF-8', '-cp',cp,'-d',tmp,
                    *[str(java/name) for name in ['Crypto.java','Protocol.java','MediaProtocol.java','MediaWire.java','AttachmentSync.java']],
                    str(root/'tests/AttachmentSyncTest.java')], check=True)
    subprocess.run([str(jdk/('java'+suffix)), '-cp',tmp+os.pathsep+cp,'io.clipbridge.android.AttachmentSyncTest'], check=True)
