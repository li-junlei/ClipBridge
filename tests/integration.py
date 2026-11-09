"""Real HTTP relay and Java/.NET AEAD interoperability checks; no third-party packages."""
import base64
import hashlib
import json
import os
from pathlib import Path
import shutil
import socket
import subprocess
import tempfile
import time
import urllib.request
import urllib.error
from concurrent.futures import ThreadPoolExecutor

ROOT = Path(__file__).resolve().parents[1]
DOTNET = shutil.which('dotnet')
passed = 0
def check(value, name):
    global passed
    assert value, name
    passed += 1
    print('PASS', name)

with tempfile.TemporaryDirectory(prefix='clipbridge-test-') as temp:
    temp = Path(temp)
    subprocess.run([DOTNET, 'run', '--project', str(ROOT/'tests/ClipBridge.Tests'), '--', str(temp/'vector.json')], check=True)
    v = json.loads((temp/'vector.json').read_text(encoding='utf-8'))
    c, e = v['config'], v['envelope']
    token_b = base64.b64encode(os.urandom(32)).decode()
    devices = [{'id': c['device'], 'tokenHash': base64.b64encode(hashlib.sha256(c['token'].encode()).digest()).decode()},
               {'id': c['peer'], 'tokenHash': base64.b64encode(hashlib.sha256(token_b.encode()).digest()).decode()}]
    (temp/'relay.json').write_text(json.dumps({'pair': c['pair'], 'devices': devices}), encoding='utf-8')
    with socket.socket() as sock:
        sock.bind(('127.0.0.1', 0))
        port = sock.getsockname()[1]
    origin = f'http://127.0.0.1:{port}'
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    env = dict(os.environ, ASPNETCORE_URLS=origin, CLIPBRIDGE_RELAY_CONFIG=str(temp/'relay.json'), CLIPBRIDGE_DATA=str(temp/'data'))
    command = [DOTNET, str(ROOT/'src/ClipBridge.Relay/bin/Release/net10.0/ClipBridge.Relay.dll')]
    def request(path, token=None, body=None):
        headers = {'Content-Type': 'application/json'}
        if token is not None:
            headers['Authorization'] = 'Bearer ' + token
        req = urllib.request.Request(origin+path, data=None if body is None else json.dumps(body).encode(), headers=headers)
        try:
            with opener.open(req, timeout=30) as r:
                data = r.read()
                return r.status, json.loads(data) if data else None
        except urllib.error.HTTPError as ex:
            return ex.code, None
    def start():
        log = open(temp/'relay-test.log', 'w', encoding='utf-8')
        process = subprocess.Popen(command, env=env, stdout=log, stderr=log)
        last_error = None
        for _ in range(100):
            try:
                result = request('/health')
                last_error = str(result)
                if result[0] == 200:
                    log.close()
                    return process
            except OSError as ex:
                last_error = repr(ex)
            if process.poll() is not None:
                raise RuntimeError('Relay exited during startup')
            time.sleep(.1)
        process.terminate()
        process.wait(timeout=10)
        log.close()
        raise RuntimeError('Relay startup timed out: ' + str(last_error) + '\n' + (temp/'relay-test.log').read_text(encoding='utf-8'))
    process = start()
    try:
        check(request('/v1/inbox')[0] == 401, 'authentication required')
        check(request('/v1/inbox', 'wrong')[0] == 401, 'bad token rejected')
        check(request('/v1/messages', token_b, e)[0] == 403, 'sender identity bound to token')
        check(request('/v1/messages', c['token'], e)[0] == 200, 'ciphertext accepted')
        check(request('/v1/messages', c['token'], e)[0] == 200, 'idempotent retry')
        check(request('/v1/inbox', token_b)[1] == e, 'recipient receives exact ciphertext')
        altered = dict(e, data=base64.b64encode(os.urandom(32)).decode())
        check(request('/v1/messages', c['token'], altered)[0] == 409, 'counter reuse rejected')
        check(request('/v1/inbox?after=invalid', token_b)[0] == 400, 'invalid cursor rejected')
        check(request('/v1/messages', c['token'], v['controls'])[0] == 200, '64 KiB escaped payload accepted over HTTP')
        check(request('/v1/inbox', token_b)[1] == v['controls'], '64 KiB ciphertext delivered without truncation')
        with ThreadPoolExecutor(max_workers=1) as pool:
            waiting = pool.submit(request, '/v1/inbox?after=' + v['controls']['id'], token_b)
            time.sleep(.2)
            check(not waiting.done(), 'long poll waits while no new message exists')
            check(request('/v1/messages', c['token'], v['nextEnvelope'])[0] == 200, 'new message accepted during long poll')
            check(waiting.result(timeout=5)[1] == v['nextEnvelope'], 'long poll wakes immediately for new ciphertext')
        stored = b''.join(p.read_bytes() for p in (temp/'data').glob('*.json'))
        check(v['text'].encode() not in stored and b'"key"' not in stored and b'"text"' not in stored, 'relay disk contains ciphertext envelope only')
    finally:
        process.terminate(); process.wait(timeout=10)
    process = start()
    try:
        check(request('/v1/inbox', token_b)[1] == v['nextEnvelope'], 'ciphertext survives relay restart')
    finally:
        process.terminate(); process.wait(timeout=10)
    java_home = os.environ.get('JAVA_HOME')
    suffix = '.exe' if os.name == 'nt' else ''
    javac = str(Path(java_home)/('bin/javac'+suffix)) if java_home else shutil.which('javac')
    java = str(Path(java_home)/('bin/java'+suffix)) if java_home else shutil.which('java')
    if not javac or not java:
        raise RuntimeError('Install JDK 17+ and set JAVA_HOME or add its bin directory to PATH.')
    subprocess.run([str(javac), '-encoding', 'UTF-8', '-d', str(temp),
                    str(ROOT/'android/app/src/main/java/io/clipbridge/android/Crypto.java'),
                    str(ROOT/'tests/CryptoInterop.java')], check=True)
    header = f"clipbridge:1:{e['pair']}:{e['from']}:{e['to']}:{e['id']}:{e['clock']}"
    result = subprocess.check_output([str(java), '-cp', str(temp), 'CryptoInterop', c['key'], e['nonce'], header, e['data']], text=True).strip()
    check(json.loads(base64.b64decode(result))['text'] == v['text'], 'Android Java and .NET decrypt/encrypt interoperability')
print(f'{passed} integration checks passed.')
