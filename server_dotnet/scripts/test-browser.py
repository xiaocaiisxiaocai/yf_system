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
from browser_step_evidence import (
    BrowserStepEvidenceError,
    snapshot_browser_evidence,
    validate_browser_step_evidence,
)
from test_host_artifacts import verify_test_host_artifacts
from test_role_fixtures import assert_admin_only_initialization, install_legacy_test_roles
from script_safety import clean_dotnet_config_environment

ROOT = Path(__file__).resolve().parents[2]
SCRIPTS = Path(__file__).resolve().parent / 'browser'
ARTIFACTS_ROOT = (ROOT / '.artifacts').resolve()
TEST_ROOT = ARTIFACTS_ROOT / 'tests'
TEST_TEMP_ROOT = TEST_ROOT / 'tmp'
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--output', type=Path)
parser.add_argument('--clamav-port', type=int, help='Use an explicitly owned real clamd on 127.0.0.1 at this port instead of Fake for OEM flows.')
parser.add_argument('--continue-on-failure', action='store_true', help='Collect independent step failures; the run still fails if any step fails.')
parser.add_argument('--steps', nargs='+', default=['auth', 'fixtures', 'users', 'management', 'accounts', 'business', 'dictionaries', 'preview-extras', 'system', 'smtp-settings', 'final', 'layout', 'project-edges', 'access', 'auth-edges', 'file-edges', 'config-member-edges', 'message-edges', 'file-controls', 'business-controls', 'collaboration', 'oem'],
    choices=['auth', 'fixtures', 'users', 'management', 'accounts', 'business', 'system', 'smtp-settings', 'final', 'layout', 'project-edges', 'access', 'auth-edges', 'file-edges', 'config-member-edges', 'message-edges', 'file-controls', 'business-controls', 'collaboration', 'dictionaries', 'preview-extras', 'oem'])
args = parser.parse_args()
if args.clamav_port is not None and not 1 <= args.clamav_port <= 65535:
    raise SystemExit('--clamav-port must be between 1 and 65535.')
if not __debug__:
    raise SystemExit('Do not run the browser suite with Python assertions disabled (-O/PYTHONOPTIMIZE).')
if args.steps[:2] != ['auth', 'fixtures']:
    raise SystemExit('Every fresh run must start with auth fixtures.')
if len(args.steps) != len(set(args.steps)):
    raise SystemExit('Run each browser step at most once.')
for step, dependencies in {'business': ['users'], 'final': ['business'], 'layout': ['business'],
                           'dictionaries': ['users'], 'preview-extras': ['users'],
                           'project-edges': ['users'], 'file-edges': ['users'], 'message-edges': ['users'],
                           'smtp-settings': ['system'], 'business-controls': ['users'], 'collaboration': ['users'], 'oem': ['fixtures']}.items():
    if step in args.steps and any(required not in args.steps[:args.steps.index(step)] for required in dependencies):
        raise SystemExit(f'{step} requires earlier steps: {", ".join(dependencies)}')
output = (args.output or TEST_ROOT / 'browser' / ('browser-' + time.strftime('%Y%m%d-%H%M%S') + '-' + secrets.token_hex(4))).resolve()
if not output.is_relative_to(TEST_ROOT) or output == TEST_ROOT:
    raise SystemExit(f'Browser evidence output must be a child directory below {TEST_ROOT}.')
if output.exists() and any(output.iterdir()):
    raise SystemExit('Use a new empty evidence directory for each independent run.')
output.mkdir(parents=True, exist_ok=True)
TEST_TEMP_ROOT.mkdir(parents=True, exist_ok=True)
url = urllib.parse.urlsplit(os.environ.get('YF_TEST_DATABASE_URL', ''))
if url.scheme != 'mysql' or url.hostname not in ('127.0.0.1', 'localhost', '::1'):
    raise SystemExit('Set YF_TEST_DATABASE_URL to an explicit local MySQL administration URL.')
runner = Path(os.environ.get('YF_PLAYWRIGHT_RUNNER', ''))
if not runner.is_file():
    raise SystemExit('Set YF_PLAYWRIGHT_RUNNER to an existing Playwright run.js; no installation is performed.')
api = ROOT / 'server_dotnet/Yf.Api'
host_dir = os.environ.get('YF_BROWSER_HOST_DIR')
dll = Path(host_dir).resolve() / 'Yf.Api.dll' if host_dir else api / 'bin/Debug/net10.0/Yf.Api.dll'
host = Path(host_dir).resolve() / 'Yf.Api.TestHost.dll' if host_dir else ROOT / 'server_dotnet/TestHost/bin/Debug/net10.0/Yf.Api.TestHost.dll'
for required in (dll, host, ROOT / 'web/dist/index.html'):
    if not required.is_file():
        raise SystemExit('Build the API, TestHost and frontend first: ' + str(required))
test_host_artifacts = verify_test_host_artifacts(dll.parent, host)


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
artifact_files = [dll, host, *test_host_artifacts, *sorted((ROOT / 'web/dist').rglob('*'))]
artifact_hashes = {str(file.relative_to(ROOT)).replace('\\', '/'): hashlib.sha256(file.read_bytes()).hexdigest()
                   for file in artifact_files if file.is_file()}
schema = 'yf_test_browser_' + secrets.token_hex(12)
connection = pymysql.connect(host=url.hostname, port=url.port or 3306,
    user=urllib.parse.unquote(url.username or ''), password=urllib.parse.unquote(url.password or ''), autocommit=True)
created = False
process = None
storage_path = None
result = {'status': 'fail', 'steps': args.steps, 'stepEvidence': [],
          'stepFailures': [], 'businessDatabaseTouched': False, 'smtpUsed': False,
          'oemScanner': 'ClamAV' if args.clamav_port else 'Fake'}
try:
    with connection.cursor() as cursor:
        cursor.execute(f'CREATE DATABASE `{schema}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci')
    created = True
    connection.select_db(schema)
    with contextlib.ExitStack() as scope:
        temporary = scope.enter_context(tempfile.TemporaryDirectory(prefix='yf_browser_', dir=TEST_TEMP_ROOT))
        storage_path = Path(temporary).resolve() / 'storage'
        storage_path.mkdir()
        oem_storage_path = Path(temporary).resolve() / 'oem-storage'
        oem_storage_path.mkdir()
        with socket.socket() as probe:
            probe.bind(('127.0.0.1', 0))
            port = probe.getsockname()[1]
        base = f'http://127.0.0.1:{port}'
        initial = 'Yf9!' + secrets.token_urlsafe(9)
        env = clean_dotnet_config_environment()
        env.update({
            'App__ConnectionString': f'Server={quoted(url.hostname)};Port={url.port or 3306};Database={schema};User ID={quoted(urllib.parse.unquote(url.username or ""))};Password={quoted(urllib.parse.unquote(url.password or ""))}',
            'App__JwtSecret': secrets.token_urlsafe(48), 'App__StorageRoot': str(storage_path),
            'App__WebBaseUrl': base, 'App__CookieSecure': 'false', 'App__WorkerEnabled': 'false',
            'App__Smtp__Host': '',
            # OEM: separate storage and TestHost-driven scan/promotion. Fake is explicit unless a real port is supplied.
            'App__OemStorageRoot': str(oem_storage_path), 'App__OemScanner__Engine': 'Fake',
            'App__OemScanner__AcknowledgeInsecureFake': 'true', 'YF_TESTHOST_OEM_SCAN': '1', 'ASPNETCORE_URLS': base, 'URLS': base, 'ASPNETCORE_WEBROOT': str(ROOT / 'web/dist'),
            'YF_BOOTSTRAP_PASSWORD': initial,
            'YF_PROJECT_ROOT': str(ROOT), 'YF_BROWSER_SUPPORT_DIR': str(SCRIPTS),
            'YF_BROWSER_EVIDENCE_DIR': str(output), 'Logging__LogLevel__Default': 'Warning',
        })
        if args.clamav_port:
            env.update({'App__OemScanner__Engine': 'ClamAV', 'App__OemScanner__ClamAv__Host': '127.0.0.1',
                        'App__OemScanner__ClamAv__Port': str(args.clamav_port),
                        'App__OemScanner__AcknowledgeInsecureFake': 'false'})
        initialized = subprocess.run(['dotnet', str(dll), '--initialize-database'], cwd=api, env=env, capture_output=True)
        if initialized.returncode:
            raise RuntimeError('Owned database initialization failed; no business database was used.')
        assert_admin_only_initialization(connection)
        print('PASS empty initialization creates only the admin user and system administrator role', flush=True)
        # Legacy role fixtures are test-only; production initialization remains admin-only.
        install_legacy_test_roles(connection)
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
                'initialPassword': initial, 'adminChangedPassword': 'Yf9!' + secrets.token_urlsafe(9),
                'oemScanner': result['oemScanner']}), encoding='utf-8')
            print(f'Owned browser host ready: {base}; no business data or SMTP', flush=True)
            for script in args.steps:
                print('RUN browser ' + script, flush=True)
                step_log_path = output / (script + '.log')
                step_before = snapshot_browser_evidence(output)
                with step_log_path.open('wb') as step_log:
                    step = subprocess.run(['node', str(runner.resolve()), str(SCRIPTS / (script + '.cjs'))],
                        cwd=ROOT, env=env, stdout=step_log, stderr=subprocess.STDOUT, timeout=600)
                if step.returncode:
                    print(step_log_path.read_text(encoding='utf-8', errors='replace')[-6000:], flush=True)
                    result['stepFailures'].append({'step': script, 'log': step_log_path.name})
                    if args.continue_on_failure and script not in ('auth', 'fixtures', 'users'):
                        continue
                    raise RuntimeError(f'Browser step failed: {script}; log: {step_log_path}')
                try:
                    evidence = validate_browser_step_evidence(
                        script, step_before, snapshot_browser_evidence(output), output)
                except BrowserStepEvidenceError as error:
                    with step_log_path.open('a', encoding='utf-8') as step_log:
                        step_log.write('\nEVIDENCE FAILURE ' + str(error) + '\n')
                    print(step_log_path.read_text(encoding='utf-8', errors='replace')[-6000:], flush=True)
                    raise RuntimeError(
                        f'Browser step evidence failed: {script}; log: {step_log_path}') from error
                result['stepEvidence'].append(evidence)
                print('PASS browser ' + script, flush=True)
            if result['stepFailures']:
                raise RuntimeError('Browser steps failed: ' + ', '.join(item['step'] for item in result['stepFailures']))
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
