"""Exercise the built React UI and ASP.NET API on an owned local MySQL schema.

Prerequisites: built web/dist and Debug API/TestHost; Python+pymysql;
an existing Playwright runner and installed Chrome. No packages are installed.
Set YF_TEST_DATABASE_URL and YF_PLAYWRIGHT_RUNNER explicitly. No real email.
"""
import argparse
import contextlib
import hashlib
import json
import os
from pathlib import Path
import secrets
import socket
import subprocess
import tempfile
import time
import urllib.parse
import urllib.request
import zipfile

import pymysql

ROOT = Path(__file__).resolve().parents[2]
SCRIPTS = Path(__file__).resolve().parent / 'browser'
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--output', type=Path)
parser.add_argument('--steps', nargs='+', default=['auth', 'fixtures', 'users', 'management', 'accounts', 'business', 'system', 'final', 'layout', 'project-edges', 'access', 'auth-edges', 'file-edges', 'config-member-edges', 'message-edges'],
    choices=['auth', 'fixtures', 'users', 'management', 'accounts', 'business', 'system', 'final', 'layout', 'project-edges', 'access', 'auth-edges', 'file-edges', 'config-member-edges', 'message-edges'])
args = parser.parse_args()
if args.steps[:2] != ['auth', 'fixtures']:
    raise SystemExit('Every fresh run must start with auth fixtures.')
if len(args.steps) != len(set(args.steps)):
    raise SystemExit('Run each browser step at most once.')
for step, dependencies in {'business': ['users'], 'final': ['business'], 'layout': ['business'],
                           'project-edges': ['users'], 'file-edges': ['users'], 'message-edges': ['users']}.items():
    if step in args.steps and any(required not in args.steps[:args.steps.index(step)] for required in dependencies):
        raise SystemExit(f'{step} requires earlier steps: {", ".join(dependencies)}')
output = (args.output or ROOT / '.runlogs' / ('browser-' + time.strftime('%Y%m%d-%H%M%S'))).resolve()
if output.exists() and any(output.iterdir()):
    raise SystemExit('Use a new empty evidence directory for each independent run.')
output.mkdir(parents=True, exist_ok=True)
url = urllib.parse.urlsplit(os.environ.get('YF_TEST_DATABASE_URL', ''))
if url.scheme != 'mysql' or url.hostname not in ('127.0.0.1', 'localhost', '::1'):
    raise SystemExit('Set YF_TEST_DATABASE_URL to an explicit local MySQL administration URL.')
runner = Path(os.environ.get('YF_PLAYWRIGHT_RUNNER', ''))
if not runner.is_file():
    raise SystemExit('Set YF_PLAYWRIGHT_RUNNER to an existing Playwright run.js; no installation is performed.')
api = ROOT / 'server_dotnet/Yf.Api'
dll = api / 'bin/Debug/net10.0/Yf.Api.dll'
host = ROOT / 'server_dotnet/TestHost/bin/Debug/net10.0/Yf.Api.TestHost.dll'
for required in (dll, host, ROOT / 'web/dist/index.html'):
    if not required.is_file():
        raise SystemExit('Build the API, TestHost and frontend first: ' + str(required))


def sources():
    files = subprocess.check_output(['git', 'ls-files', '-z', '--cached', '--others', '--exclude-standard', '--', 'server_dotnet', 'web'], cwd=ROOT).decode().split('\0')
    return {name: hashlib.sha256((ROOT / name).read_bytes()).hexdigest() for name in files
            if name and Path(name).suffix in ('.cs', '.csproj', '.json', '.py', '.ts', '.tsx', '.cjs', '.ps1', '.css', '.html', '.svg')}


def quoted(value):
    return '"' + str(value).replace('"', '""') + '"'


def stop_owned(owned):
    if owned.poll() is None:
        owned.terminate()
        try:
            owned.wait(timeout=15)
        except subprocess.TimeoutExpired:
            owned.kill()
            owned.wait()


before = sources()
artifact_files = [dll, host, *sorted((ROOT / 'web/dist').rglob('*'))]
artifact_hashes = {str(file.relative_to(ROOT)).replace('\\', '/'): hashlib.sha256(file.read_bytes()).hexdigest()
                   for file in artifact_files if file.is_file()}
schema = 'yf_test_browser_' + secrets.token_hex(12)
connection = pymysql.connect(host=url.hostname, port=url.port or 3306,
    user=urllib.parse.unquote(url.username or ''), password=urllib.parse.unquote(url.password or ''), autocommit=True)
created = False
process = None
storage_path = None
result = {'status': 'fail', 'steps': args.steps, 'businessDatabaseTouched': False, 'smtpUsed': False}
try:
    with connection.cursor() as cursor:
        cursor.execute(f'CREATE DATABASE `{schema}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci')
    created = True
    connection.select_db(schema)
    with contextlib.ExitStack() as scope:
        temporary = scope.enter_context(tempfile.TemporaryDirectory(prefix='yf_browser_'))
        storage_path = Path(temporary).resolve() / 'storage'
        storage_path.mkdir()
        with socket.socket() as probe:
            probe.bind(('127.0.0.1', 0))
            port = probe.getsockname()[1]
        base = f'http://127.0.0.1:{port}'
        initial = 'Yf9!' + secrets.token_urlsafe(9)
        env = os.environ.copy()
        env.pop('YF_CONFIG_PATH', None)
        env.update({
            'App__ConnectionString': f'Server={quoted(url.hostname)};Port={url.port or 3306};Database={schema};User ID={quoted(urllib.parse.unquote(url.username or ""))};Password={quoted(urllib.parse.unquote(url.password or ""))}',
            'App__JwtSecret': secrets.token_urlsafe(48), 'App__StorageRoot': str(storage_path),
            'App__WebBaseUrl': base, 'App__CookieSecure': 'false', 'App__WorkerEnabled': 'false',
            'App__Smtp__Host': '', 'ASPNETCORE_URLS': base, 'URLS': base, 'ASPNETCORE_WEBROOT': str(ROOT / 'web/dist'),
            'YF_BOOTSTRAP_PASSWORD': initial,
            'YF_PROJECT_ROOT': str(ROOT), 'YF_BROWSER_SUPPORT_DIR': str(SCRIPTS),
            'YF_BROWSER_EVIDENCE_DIR': str(output), 'Logging__LogLevel__Default': 'Warning',
        })
        initialized = subprocess.run(['dotnet', str(dll), '--initialize-database'], cwd=api, env=env, capture_output=True)
        if initialized.returncode:
            raise RuntimeError('Owned database initialization failed; no business database was used.')
        env.pop('YF_BOOTSTRAP_PASSWORD')
        with (output / 'api.log').open('wb') as log:
            process = subprocess.Popen(['dotnet', str(host)], cwd=api, env=env, stdout=log, stderr=log,
                creationflags=subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0)
            scope.callback(stop_owned, process)
            for _ in range(150):
                if process.poll() is not None:
                    raise RuntimeError('Owned TestHost exited before health check.')
                try:
                    if json.load(urllib.request.urlopen(base + '/health', timeout=2)) == {'status': 'ok', 'db': 'up'}:
                        break
                except OSError:
                    pass
                time.sleep(.1)
            else:
                raise RuntimeError('Owned TestHost health check timed out.')
            (output / 'state.private.json').write_text(json.dumps({'base': base,
                'initialPassword': initial, 'adminChangedPassword': 'Yf9!' + secrets.token_urlsafe(9)}), encoding='utf-8')
            print(f'Owned browser host ready: {base}; no business data or SMTP', flush=True)
            for script in args.steps:
                print('RUN browser ' + script, flush=True)
                with (output / (script + '.log')).open('wb') as step_log:
                    step = subprocess.run(['node', str(runner.resolve()), str(SCRIPTS / (script + '.cjs'))],
                        cwd=ROOT, env=env, stdout=step_log, stderr=subprocess.STDOUT, timeout=600)
                if step.returncode:
                    print((output / (script + '.log')).read_text(encoding='utf-8', errors='replace')[-6000:], flush=True)
                    raise RuntimeError('Browser step failed: ' + script)
                print('PASS browser ' + script, flush=True)
            fixtures = json.loads((output / 'fixtures.private.json').read_text(encoding='utf-8'))
            persisted = []
            for project in fixtures.get('uiProjects', {}).values():
                with connection.cursor() as cursor:
                    cursor.execute('SELECT status FROM projects WHERE id=%s', (project['id'],))
                    assert cursor.fetchone()[0] == 'COMPLETED'
                    cursor.execute('SELECT COUNT(*) FROM files WHERE project_id=%s', (project['id'],))
                    assert cursor.fetchone()[0] == 2
                    cursor.execute('SELECT COUNT(*) FROM messages WHERE project_id=%s', (project['id'],))
                    assert cursor.fetchone()[0] == 2
                persisted.append({'projectId': project['id'], 'status': 'COMPLETED', 'files': 2, 'messages': 2})
            zip_integrity = None
            if 'final' in args.steps:
                with zipfile.ZipFile(output / 'browser-batch.zip') as archive:
                    assert archive.testzip() is None
                    assert len(archive.infolist()) == 2
                    for name in ('valid-preview.pdf', 'vendor-response.xlsx'):
                        assert hashlib.sha256(archive.read(name)).digest() == hashlib.sha256((output / name).read_bytes()).digest()
                zip_integrity = True
            assert before == sources(), 'Source changed during browser validation; rerun affected evidence.'
            assert all(hashlib.sha256((ROOT / name).read_bytes()).hexdigest() == digest
                       for name, digest in artifact_hashes.items()), 'Built artifacts changed during browser validation.'
            operations_file = output / 'browser-operations.json'
            operations = json.loads(operations_file.read_text(encoding='utf-8')) if operations_file.exists() else []
            bootstrap = json.loads((output / 'browser-results.json').read_text(encoding='utf-8'))['checks']
            assert all(check['status'] == 'pass' for check in operations + bootstrap)
            for name in ('page-errors.jsonl', 'http-errors.jsonl'):
                assert not (output / name).exists() or not (output / name).read_text(encoding='utf-8').strip(), name
            result.update(status='pass', browserOperationGroups=len(operations) + len(bootstrap),
                persisted=persisted, zipIntegrity=zip_integrity, sourceHashes=before, artifactHashes=artifact_hashes)
finally:
    if process is not None:
        stop_owned(process)
    if created:
        with connection.cursor() as cursor:
            cursor.execute(f'DROP DATABASE `{schema}`')
    connection.close()
    for private in output.glob('*.private.json'):
        assert private.resolve().parent == output
        private.unlink()
    result['cleanup'] = {'ownedProcessExited': process is None or process.poll() is not None,
        'ownedDatabaseDropped': created, 'ownedStorageRemoved': storage_path is None or not storage_path.exists(),
        'privateStateRemoved': not any(output.glob('*.private.json'))}
    (output / 'results.json').write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
    print('Owned browser resources cleaned; result: ' + result['status'], flush=True)
