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
import shutil
import socket
import subprocess
import sys
import tempfile
import time
import urllib.error
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
parser.add_argument('--continue-on-failure', action='store_true', help='Collect independent step failures; the run still fails if any step fails.')
parser.add_argument('--steps', nargs='+', default=['auth', 'fixtures', 'users', 'management', 'accounts', 'business', 'dictionaries', 'preview-extras', 'system', 'smtp-settings', 'final', 'layout', 'project-edges', 'access', 'auth-edges', 'file-edges', 'config-member-edges', 'message-edges', 'file-controls', 'business-controls', 'collaboration'],
    choices=['auth', 'fixtures', 'users', 'management', 'accounts', 'business', 'system', 'smtp-settings', 'final', 'layout', 'project-edges', 'access', 'auth-edges', 'file-edges', 'config-member-edges', 'message-edges', 'file-controls', 'business-controls', 'collaboration', 'dictionaries', 'preview-extras'])
args = parser.parse_args()
if not __debug__:
    raise SystemExit('Do not run the browser suite with Python assertions disabled (-O/PYTHONOPTIMIZE).')
if args.steps[:2] != ['auth', 'fixtures']:
    raise SystemExit('Every fresh run must start with auth fixtures.')
if len(args.steps) != len(set(args.steps)):
    raise SystemExit('Run each browser step at most once.')
for step, dependencies in {'business': ['users'], 'final': ['business'], 'layout': ['business'],
                           'dictionaries': ['users'], 'preview-extras': ['users'],
                           'project-edges': ['users'], 'file-edges': ['users'], 'message-edges': ['users'],
                           'smtp-settings': ['system'], 'business-controls': ['users'], 'collaboration': ['users']}.items():
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
dll = Path(host_dir).resolve() / 'Yf.Api.dll' if host_dir else api / 'bin/Debug/net8.0/Yf.Api.dll'
host = Path(host_dir).resolve() / 'Yf.Api.TestHost.dll' if host_dir else ROOT / 'server_dotnet/TestHost/bin/Debug/net8.0/Yf.Api.TestHost.dll'
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


def prepare_content_root(parent):
    """Owned, otherwise empty ASP.NET content root for the child API processes.

    appsettings.json and the git-ignored developer appsettings.Local.json are resolved relative to
    the content root; running from server_dotnet/Yf.Api would leak local database/JWT/URL settings.
    Only the build output's committed appsettings.json (build output never contains the Local file)
    is copied; everything else comes from the process-scoped App__* overrides below.
    """
    content_root = Path(parent) / 'content-root'
    content_root.mkdir()
    shipped = dll.parent / 'appsettings.json'
    if shipped.is_file():
        shutil.copy2(shipped, content_root / 'appsettings.json')
    return content_root


def wait_for_health(owned, base, timeout_seconds=120.0):
    # Loopback only: never route readiness through a system/user proxy.
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    deadline = time.monotonic() + timeout_seconds
    last = 'no response'
    while time.monotonic() < deadline:
        if owned.poll() is not None:
            raise RuntimeError(f'Owned TestHost exited before health check (code {owned.returncode}).')
        try:
            with opener.open(base + '/health', timeout=5) as response:
                body = response.read()
            if json.loads(body) == {'status': 'ok', 'db': 'up'}:
                return
            last = 'unexpected health body'
        except urllib.error.HTTPError as error:
            last = f'HTTP {error.code}'
        except (OSError, ValueError) as error:
            # Connection refused while starting, a timeout, or a non-JSON body (e.g. a proxy page).
            last = type(error).__name__
        time.sleep(.2)
    raise RuntimeError(f'Owned TestHost health check timed out after {timeout_seconds:g}s; last: {last}')


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
          'stepFailures': [], 'businessDatabaseTouched': False, 'smtpUsed': False}
try:
    with connection.cursor() as cursor:
        cursor.execute(f'CREATE DATABASE `{schema}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci')
    created = True
    connection.select_db(schema)
    with contextlib.ExitStack() as scope:
        temporary = scope.enter_context(tempfile.TemporaryDirectory(prefix='yf_browser_', dir=TEST_TEMP_ROOT))
        storage_path = Path(temporary).resolve() / 'storage'
        storage_path.mkdir()
        content_root = prepare_content_root(Path(temporary).resolve())
        with socket.socket() as probe:
            probe.bind(('127.0.0.1', 0))
            port = probe.getsockname()[1]
        base = f'http://127.0.0.1:{port}'
        initial = 'Yf9!' + secrets.token_urlsafe(9)
        # The node/Playwright steps only need the evidence/support/project paths; they must not
        # inherit the owned database administration connection string or the JWT secret.
        node_env = clean_dotnet_config_environment()
        node_env.update({
            'YF_PROJECT_ROOT': str(ROOT), 'YF_BROWSER_SUPPORT_DIR': str(SCRIPTS),
            'YF_BROWSER_EVIDENCE_DIR': str(output),
        })
        env = clean_dotnet_config_environment()
        for inherited in ('ASPNETCORE_CONTENTROOT', 'DOTNET_CONTENTROOT', 'DOTNET_URLS'):
            env.pop(inherited, None)
        env.update({
            'App__ConnectionString': f'Server={quoted(url.hostname)};Port={url.port or 3306};Database={schema};User ID={quoted(urllib.parse.unquote(url.username or ""))};Password={quoted(urllib.parse.unquote(url.password or ""))}',
            'App__JwtSecret': secrets.token_urlsafe(48), 'App__StorageRoot': str(storage_path),
            'App__WebBaseUrl': base, 'App__CookieSecure': 'false', 'App__WorkerEnabled': 'false',
            'App__Smtp__Host': '',
            'ASPNETCORE_URLS': base, 'URLS': base, 'ASPNETCORE_WEBROOT': str(ROOT / 'web/dist'),
            'ASPNETCORE_CONTENTROOT': str(content_root),
            'YF_BOOTSTRAP_PASSWORD': initial, 'Logging__LogLevel__Default': 'Warning',
        })
        initialized = subprocess.run(['dotnet', str(dll), '--initialize-database'], cwd=content_root, env=env,
                                     capture_output=True, timeout=300)
        if initialized.returncode:
            raise RuntimeError('Owned database initialization failed; no business database was used: '
                               + initialized.stderr.decode(errors='replace')[-1500:])
        assert_admin_only_initialization(connection)
        print('PASS empty initialization creates only the admin user and system administrator role', flush=True)
        # Legacy role fixtures are test-only; production initialization remains admin-only.
        install_legacy_test_roles(connection)
        env.pop('YF_BOOTSTRAP_PASSWORD')
        with (output / 'api.log').open('wb') as log:
            process = subprocess.Popen(['dotnet', str(host)], cwd=content_root, env=env, stdout=log, stderr=log,
                creationflags=subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0)
            scope.callback(stop_owned, process)
            wait_for_health(process, base)
            (output / 'state.private.json').write_text(json.dumps({'base': base,
                'initialPassword': initial, 'adminChangedPassword': 'Yf9!' + secrets.token_urlsafe(9)}), encoding='utf-8')
            print(f'Owned browser host ready: {base}; no business data or SMTP', flush=True)
            for script in args.steps:
                print('RUN browser ' + script, flush=True)
                step_log_path = output / (script + '.log')
                step_before = snapshot_browser_evidence(output)
                with step_log_path.open('wb') as step_log:
                    step = subprocess.run(['node', str(runner.resolve()), str(SCRIPTS / (script + '.cjs'))],
                        cwd=ROOT, env=node_env, stdout=step_log, stderr=subprocess.STDOUT, timeout=600)
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
                    assert cursor.fetchone()[0] == 3
                    cursor.execute('SELECT COUNT(*) FROM messages WHERE project_id=%s', (project['id'],))
                    assert cursor.fetchone()[0] == 2
                persisted.append({'projectId': project['id'], 'status': 'COMPLETED', 'files': 3, 'messages': 2})
            zip_integrity = None
            if 'final' in args.steps:
                with zipfile.ZipFile(output / 'browser-batch.zip') as archive:
                    assert archive.testzip() is None
                    assert len(archive.infolist()) == 3
                    for name in ('valid-preview.pdf', 'vendor-response.xlsx', 'assembly-3d.step'):
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
    # Every cleanup step runs on its own: one failure must not skip dropping the database,
    # deleting private state (passwords) or writing results.json.
    cleanup_errors = []
    database_dropped = False

    def cleanup_step(label, action):
        try:
            action()
        except Exception as error:  # noqa: BLE001 - report and continue with the remaining cleanup
            cleanup_errors.append(f'{label}: {type(error).__name__}: {error}')

    if process is not None:
        cleanup_step('stop owned host', lambda: stop_owned(process))

    def drop_database():
        global database_dropped
        with connection.cursor() as cursor:
            cursor.execute(f'DROP DATABASE `{schema}`')
        database_dropped = True

    if created:
        cleanup_step('drop owned database', drop_database)
    cleanup_step('close database connection', connection.close)
    def remove_private(private):
        if private.resolve().parent != output:
            raise RuntimeError('private state escaped the evidence directory')
        private.unlink()

    for private in list(output.glob('*.private.json')):
        cleanup_step('remove ' + private.name, lambda private=private: remove_private(private))
    if cleanup_errors:
        result['status'] = 'fail'
        result['cleanupErrors'] = cleanup_errors
    result['cleanup'] = {'ownedProcessExited': process is None or process.poll() is not None,
        'ownedDatabaseDropped': database_dropped or not created,
        'ownedStorageRemoved': storage_path is None or not storage_path.exists(),
        'privateStateRemoved': not any(output.glob('*.private.json'))}
    (output / 'results.json').write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
    print('Owned browser resources cleaned; result: ' + result['status'], flush=True)
    for cleanup_error in cleanup_errors:
        print('CLEANUP FAILURE ' + cleanup_error, flush=True)
    if cleanup_errors and sys.exc_info()[0] is None:
        raise SystemExit('Browser run cleanup failed: ' + '; '.join(cleanup_errors))
