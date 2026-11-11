"""Real group crypto/state machine with a deterministic in-memory transport."""
from pathlib import Path
import os, subprocess, tempfile
root=Path(__file__).resolve().parents[1]
jdk=Path(os.environ.get('JAVA_HOME',r'C:/Program Files/Android/Android Studio/jbr'))/'bin'
cache=Path(os.environ.get('GRADLE_USER_HOME',Path.home()/'.gradle'))/'caches/modules-2/files-2.1'
jars=[root/'artifacts/test-deps/json-20250517.jar']
choices=list((cache/'org.bouncycastle/bcprov-jdk18on').rglob('*.jar'))
if not choices:raise RuntimeError('Build Android first')
jars.append(max(choices,key=lambda p:p.stat().st_mtime));cp=os.pathsep.join(map(str,jars))
java=root/'android/app/src/main/java/io/clipbridge/android';base=root/'work/test-tmp';base.mkdir(parents=True,exist_ok=True)
with tempfile.TemporaryDirectory(prefix='group-completion-',dir=base) as tmp:
    suffix='.exe' if os.name=='nt' else ''
    subprocess.run([str(jdk/('javac'+suffix)),'-encoding','UTF-8','-cp',cp,'-d',tmp,
                    *[str(java/n) for n in ['Crypto.java','Protocol.java','MediaProtocol.java','GroupCrypto.java','GroupPake.java','GroupSession.java','GroupStateOps.java']],
                    str(root/'tests/group-session-fixture/GroupNetwork.java'),str(root/'tests/GroupSessionBackTest.java')],check=True)
    subprocess.run([str(jdk/('java'+suffix)),'-cp',tmp+os.pathsep+cp,'io.clipbridge.android.GroupSessionBackTest'],check=True)
