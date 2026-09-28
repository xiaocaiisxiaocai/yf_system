"""Run the real ASP.NET API against an owned disposable MySQL database and storage.

Requires Python 3.11+, pymysql, and a built Yf.Api. No real email is sent.
Connection credentials stay in process memory; only test names/results are printed.
"""
import hashlib
import contextlib
from collections import defaultdict, deque
import http.cookiejar
import io
import json
import os
from pathlib import Path
import secrets
import socket
import shutil
import subprocess
import sys
import tempfile
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
import zipfile
import pymysql
from test_host_artifacts import verify_test_host_artifacts
from test_identity_contracts import run_identity_checks
from test_file_contracts import run_file_checks, run_upload_material_checks
from test_native_download_contracts import run_native_download_checks
from test_background_copy_contracts import run_background_copy_checks
from test_role_fixtures import assert_admin_only_initialization, install_legacy_test_roles
from test_system_contracts import run_system_checks
from test_project_remediation import run_project_remediation_checks
from test_route_contracts import run_anonymous_route_contracts, run_recent_route_contracts
from test_collaboration_contracts import run_collaboration_checks
from test_business_acceptance import _create_project_group, run_business_acceptance
from test_workflow_acceptance import run_workflow_acceptance
from test_manual_supplier_roles import run_manual_supplier_role_checks
from upload_contract import init_upload, put_chunk, submit_md5

ROOT = Path(__file__).resolve().parents[2]
ARTIFACTS_ROOT = (ROOT / ".artifacts").resolve()
TEST_ROOT = ARTIFACTS_ROOT / "tests"
TEST_TEMP_ROOT = TEST_ROOT / "tmp"
TEST_TEMP_ROOT.mkdir(parents=True, exist_ok=True)
CONFIGURATION = os.environ.get("YF_TEST_CONFIGURATION", "Debug").strip()
if CONFIGURATION not in {"Debug", "Release", "Hardening", "Migration", "Redesign"}:
    raise SystemExit("YF_TEST_CONFIGURATION must be exactly Debug, Release, Hardening, Migration, or Redesign")
PUBLISHED = os.environ.get("YF_TEST_API_DIR")
API = Path(PUBLISHED).resolve() if PUBLISHED else ROOT / "server_dotnet/Yf.Api"
DLL = API / "Yf.Api.dll" if PUBLISHED else API / f"bin/{CONFIGURATION}/net8.0/Yf.Api.dll"
TEST_HOST_SETTING = os.environ.get("YF_TEST_HOST_PATH")
if TEST_HOST_SETTING:
    configured_test_host = Path(TEST_HOST_SETTING)
    TEST_HOST = (
        configured_test_host.resolve()
        if configured_test_host.is_absolute()
        else (ROOT / "server_dotnet" / configured_test_host).resolve()
    )
else:
    TEST_HOST = ROOT / f"server_dotnet/TestHost/bin/{CONFIGURATION}/net8.0/Yf.Api.TestHost.dll"
checks = []
FILES_ONLY = sys.argv[1:] == ["--files-only"]
if sys.argv[1:] and not FILES_ONLY:
    raise SystemExit("Usage: test-isolated.py [--files-only]")

default_report = TEST_ROOT / (
    "dotnet-published-results.json" if PUBLISHED else
    "dotnet-file-results.json" if FILES_ONLY else
    "dotnet-isolated-results.json"
)
report = Path(os.environ.get("YF_TEST_RESULTS_PATH", default_report)).resolve()
if not report.is_relative_to(ARTIFACTS_ROOT):
    raise SystemExit(f"YF_TEST_RESULTS_PATH must stay inside the project artifact root: {ARTIFACTS_ROOT}")
api_log_path = report.with_name(report.stem + ".api.log")


class FileContractsComplete(Exception):
    pass

if not __debug__:
    raise SystemExit("Do not run the regression suite with Python assertions disabled (-O/PYTHONOPTIMIZE).")
if not PUBLISHED:
    # Reject an out-of-date project-reference copy before creating any fixture database.
    verify_test_host_artifacts(DLL.parent, TEST_HOST)


def check(name, condition):
    if not condition:
        raise AssertionError(name)
    checks.append(name)
    print("PASS " + name, flush=True)


class LoginRequestBudget:
    """Process-local rolling budget shared by every HTTP fixture client."""

    def __init__(self, ip_limit=50, account_limit=8, window_seconds=60.0,
                 clock=time.monotonic, sleep=time.sleep):
        self.ip_limit = ip_limit
        self.account_limit = account_limit
        self.window_seconds = window_seconds
        self.clock = clock
        self.sleep = sleep
        self.lock = threading.Lock()
        self.by_base = defaultdict(deque)
        self.by_account = defaultdict(deque)

    def wait(self, base, employee_no):
        account_key = (base, employee_no.strip().casefold())
        while True:
            with self.lock:
                now = self.clock()
                cutoff = now - self.window_seconds
                base_attempts = self.by_base[base]
                account_attempts = self.by_account[account_key]
                while base_attempts and base_attempts[0] <= cutoff:
                    base_attempts.popleft()
                while account_attempts and account_attempts[0] <= cutoff:
                    account_attempts.popleft()
                delays = []
                if len(base_attempts) >= self.ip_limit:
                    delays.append(base_attempts[0] + self.window_seconds - now)
                if len(account_attempts) >= self.account_limit:
                    delays.append(account_attempts[0] + self.window_seconds - now)
                if not delays:
                    base_attempts.append(now)
                    account_attempts.append(now)
                    return
                delay = max(delays)
            self.sleep(max(delay, 0.001))

    def record_without_waiting(self, base, employee_no):
        """Track an intentional 429 probe without delaying the probe itself."""
        account_key = (base, employee_no.strip().casefold())
        with self.lock:
            now = self.clock()
            cutoff = now - self.window_seconds
            base_attempts = self.by_base[base]
            account_attempts = self.by_account[account_key]
            while base_attempts and base_attempts[0] <= cutoff:
                base_attempts.popleft()
            while account_attempts and account_attempts[0] <= cutoff:
                account_attempts.popleft()
            base_attempts.append(now)
            account_attempts.append(now)


LOGIN_REQUEST_BUDGET = LoginRequestBudget()


def login_employee_no(method, path, body):
    if (method == "POST" and path == "/api/v1/auth/login"
            and isinstance(body, dict) and isinstance(body.get("employeeNo"), str)):
        return body["employeeNo"]
    return None


def login_pacing_mode(method, path, body, expected):
    employee_no = login_employee_no(method, path, body)
    if employee_no is None:
        return None, None
    return ("record" if expected == 429 else "wait"), employee_no


def verify_login_request_budget():
    class FakeClock:
        def __init__(self):
            self.now = 0.0
            self.sleeps = []

        def clock(self):
            return self.now

        def sleep(self, delay):
            self.sleeps.append(delay)
            self.now += delay

    ip_clock = FakeClock()
    ip_budget = LoginRequestBudget(2, 2, 10.0, ip_clock.clock, ip_clock.sleep)
    ip_budget.wait("http://fixture", "first")
    ip_budget.wait("http://fixture", "second")
    ip_budget.wait("http://fixture", "third")
    account_clock = FakeClock()
    account_budget = LoginRequestBudget(10, 1, 10.0, account_clock.clock, account_clock.sleep)
    account_budget.wait("http://fixture", "same")
    account_budget.wait("http://fixture", " SAME ")
    probe_clock = FakeClock()
    probe_budget = LoginRequestBudget(1, 1, 10.0, probe_clock.clock, probe_clock.sleep)
    probe_budget.record_without_waiting("http://fixture", "probe")
    probe_budget.wait("http://fixture", "probe")
    check("HTTP fixture login budget is shared, rolling, and skips explicit 429 probes",
          ip_clock.sleeps == [10.0] and account_clock.sleeps == [10.0]
          and probe_clock.sleeps == [10.0]
          and login_pacing_mode("POST", "/api/v1/auth/login", {"employeeNo": "a"}, 200) == ("wait", "a")
          and login_pacing_mode("POST", "/api/v1/auth/login", {"employeeNo": "a"}, 401) == ("wait", "a")
          and login_pacing_mode("POST", "/api/v1/auth/login", {"employeeNo": "a"}, 429) == ("record", "a")
          and login_pacing_mode("GET", "/api/v1/auth/login", {"employeeNo": "a"}, 200) == (None, None))


class Client:
    def __init__(self, base):
        self.base = base
        self.cookies = http.cookiejar.CookieJar()
        # Test targets are always loopback. Never let host proxy settings route
        # readiness or contract traffic away from the owned child process.
        self.opener = urllib.request.build_opener(
            urllib.request.ProxyHandler({}),
            urllib.request.HTTPCookieProcessor(self.cookies),
        )
        self.token = None

    def call(self, method, path, body=None, expected=200, headers=None, raw=False):
        pacing_mode, login_account = login_pacing_mode(method, path, body, expected)
        if pacing_mode == "record":
            LOGIN_REQUEST_BUDGET.record_without_waiting(self.base, login_account)
        elif pacing_mode == "wait":
            LOGIN_REQUEST_BUDGET.wait(self.base, login_account)
        req_headers = {"Origin": self.base}
        if self.token:
            req_headers["Authorization"] = "Bearer " + self.token
        if body is not None and not isinstance(body, bytes):
            body = json.dumps(body).encode()
            req_headers["Content-Type"] = "application/json"
        req_headers.update(headers or {})
        req = urllib.request.Request(self.base + path, data=body, headers=req_headers, method=method)
        try:
            response = self.opener.open(req, timeout=30)
        except urllib.error.HTTPError as e:
            response = e
        data = response.read()
        if response.status != expected:
            safe = data.decode(errors="replace")[:500] if response.headers.get("Content-Type", "").startswith("application/json") else "non-JSON response"
            raise AssertionError(f"{method} {path}: expected {expected}, got {response.status}: {safe}")
        if raw:
            return data, response.headers
        return json.loads(data) if data else None

    def login(self, username, password):
        result = self.call("POST", "/api/v1/auth/login", {"employeeNo": username, "password": password})
        self.token = result["accessToken"]
        return result


verify_login_request_budget()


config_url = os.environ.get("YF_TEST_DATABASE_URL")
if not config_url:
    raise SystemExit("Set process-scoped YF_TEST_DATABASE_URL to a local MySQL test-administration URL; no project configuration is discovered.")
url = urllib.parse.urlsplit(config_url)
if url.scheme != "mysql" or url.hostname not in ("127.0.0.1", "localhost", "::1"):
    raise SystemExit("Isolated testing only allows local MySQL")
name = "yf_test_" + uuid.uuid4().hex[:24]
user = urllib.parse.unquote(url.username or "")
password = urllib.parse.unquote(url.password or "")
conn = pymysql.connect(host=url.hostname, port=url.port or 3306, user=user, password=password, autocommit=True)
created, process = False, None


def cs(value):
    return '"' + str(value).replace('"', '""') + '"'


def stop_process(owned):
    if owned.poll() is None:
        owned.terminate()
        try:
            owned.wait(timeout=15)
        except subprocess.TimeoutExpired:
            owned.kill()
            owned.wait()


def free_loopback_url():
    with socket.socket() as listener:
        listener.bind(("127.0.0.1", 0))
        return f"http://127.0.0.1:{listener.getsockname()[1]}"


def prepare_content_root(parent):
    """An owned, otherwise empty ASP.NET content root for every child API process.

    The API resolves appsettings.json and the optional, git-ignored appsettings.Local.json relative
    to its content root (the working directory by default). Running from Yf.Api would load a
    developer's local database/JWT/URL settings into the suite. Only the build output's committed
    appsettings.json is copied here (appsettings.Local.json is never copied to build output), so
    tests see the shipped defaults plus the process-scoped App__* overrides and nothing else.
    """
    content_root = Path(parent) / "content-root"
    content_root.mkdir()
    shipped = DLL.parent / "appsettings.json"
    if shipped.is_file():
        shutil.copy2(shipped, content_root / "appsettings.json")
    return content_root


def api_output(completed):
    return ((completed.stdout or b"").decode(errors="replace") + "\n"
            + (completed.stderr or b"").decode(errors="replace"))


def refused_with(completed, expected_text):
    """A refusal only counts when the process failed for the documented reason (not e.g. a port conflict)."""
    return completed.returncode != 0 and expected_text in api_output(completed)


def wait_for_http_ready(process, probe, target, timeout_seconds=60.0, interval_seconds=0.1):
    deadline = time.monotonic() + timeout_seconds
    attempts = 0
    last_failure = "probe returned no ready result"
    while time.monotonic() < deadline:
        return_code = process.poll()
        if return_code is not None:
            raise RuntimeError(
                f"API process {process.pid} exited before readiness with code {return_code}; "
                f"target={target}; attempts={attempts}; lastProbe={last_failure}"
            )
        attempts += 1
        try:
            if probe():
                return
            last_failure = "probe returned no ready result"
        except (OSError, urllib.error.URLError, AssertionError, ValueError) as error:
            detail = " ".join(str(error).split())
            last_failure = f"{type(error).__name__}: {detail[:600]}"
        time.sleep(interval_seconds)
    raise TimeoutError(
        f"API process {process.pid} did not become ready within {timeout_seconds:g} seconds; "
        f"target={target}; attempts={attempts}; lastProbe={last_failure}"
    )


try:
    with conn.cursor() as cursor:
        cursor.execute(f"CREATE DATABASE `{name}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci")
        created = True
    conn.select_db(name)
    with tempfile.TemporaryDirectory(prefix="yf_dotnet_test_", dir=TEST_TEMP_ROOT) as temp, contextlib.ExitStack() as stack:
        storage = Path(temp) / "storage"
        storage.mkdir()
        with socket.socket() as listener:
            listener.bind(("127.0.0.1", 0))
            port = listener.getsockname()[1]
        base = f"http://127.0.0.1:{port}"
        initial = "Yf9!" + secrets.token_urlsafe(9)
        changed = "Yf9!" + secrets.token_urlsafe(9)
        content_root = prepare_content_root(temp)
        env = {
            key: value for key, value in os.environ.items()
            if not key.lower().startswith(("app__", "app:"))
            and key.upper() not in {
                "YF_CONFIG_PATH", "YF_BOOTSTRAP_PASSWORD", "ASPNETCORE_CONTENTROOT", "ASPNETCORE_WEBROOT",
                "DOTNET_CONTENTROOT", "ASPNETCORE_URLS", "DOTNET_URLS", "URLS",
            }
        }
        env.update({"App__ConnectionString": f"Server={cs(url.hostname)};Port={url.port or 3306};Database={name};User ID={cs(user)};Password={cs(password)}",
                    "App__JwtSecret": secrets.token_urlsafe(48), "App__StorageRoot": str(storage),
                    "App__WebBaseUrl": base, "App__CookieSecure": "false", "App__WorkerEnabled": "false",
                    "App__CopyWorkerEnabled": "true",
                    "App__Smtp__Host": "", "ASPNETCORE_URLS": base, "YF_BOOTSTRAP_PASSWORD": initial,
                    "ASPNETCORE_CONTENTROOT": str(content_root),
                    "Logging__LogLevel__Default": "Warning",
                    "Logging__LogLevel__Microsoft.Hosting.Lifetime": "Information"})
        if PUBLISHED:
            # The content root is the owned empty directory; the published SPA is still served.
            env["ASPNETCORE_WEBROOT"] = str(API / "wwwroot")

        def run_api(*arguments, timeout):
            # A fresh explicit --urls per one-shot run: a leaked "Urls" setting or an occupied port
            # can never make a refusal check pass, and nothing binds the shared test port.
            return subprocess.run(
                ["dotnet", str(DLL), *arguments, "--urls", free_loopback_url()],
                cwd=content_root, env=env, capture_output=True, timeout=timeout,
            )

        initialized = run_api("--initialize-database", timeout=300)
        if initialized.returncode:
            raise RuntimeError(".NET empty database initialization failed: " + initialized.stderr.decode(errors="replace")[:1500])
        check("standalone empty database initialization", True)
        assert_admin_only_initialization(conn)
        check("empty initialization creates only the admin user and system administrator role", True)
        refused = run_api("--initialize-database", timeout=120)
        check("initializer refuses nonempty database",
              refused_with(refused, "Initialization refused: target database is not empty"))
        del env["YF_BOOTSTRAP_PASSWORD"]
        # Legacy role fixtures are test-only; production initialization remains admin-only.
        install_legacy_test_roles(conn)
        with conn.cursor() as cursor:
            cursor.execute("SELECT password_hash FROM users WHERE employee_no='admin'")
            preserved_hash = cursor.fetchone()[0]
            cursor.execute(
                "SELECT MigrationId,ProductVersion FROM __EFMigrationsHistory ORDER BY MigrationId"
            )
            expected_history = cursor.fetchall()
            cursor.execute(
                "SELECT COUNT(*) FROM project_dictionaries "
                "WHERE type='PRIORITY' AND name IN ('高','普通','低')"
            )
            check("fresh EF initialization seeds the default priorities", cursor.fetchone()[0] == 3)
        # Every EF migration in the source tree, in order: InitialCreate first, then subsequent migrations.
        source_migrations = sorted(
            path.stem for path in (Path(__file__).resolve().parents[1] / "Yf.Api/Infrastructure/Migrations").glob("*.cs")
            if path.stem[:14].isdigit() and path.stem[14:15] == "_" and "." not in path.stem)
        check("fresh EF initialization records every source migration in order, starting with InitialCreate",
              bool(source_migrations) and source_migrations[0].endswith("_InitialCreate")
              and [row[0] for row in expected_history] == source_migrations)

        for _ in range(2):
            migration = run_api("--migrate-database", timeout=300)
            if migration.returncode:
                raise RuntimeError(
                    "EF migration on current database failed: "
                    + migration.stderr.decode(errors="replace")[:1500]
                )
        with conn.cursor() as cursor:
            cursor.execute("SELECT password_hash FROM users WHERE employee_no='admin'")
            check("repeatable EF migration preserves initialized users",
                  cursor.fetchone()[0] == preserved_hash)
            cursor.execute(
                "SELECT MigrationId,ProductVersion FROM __EFMigrationsHistory ORDER BY MigrationId"
            )
            check("repeatable EF migration preserves exact history",
                  cursor.fetchall() == expected_history)

            # Corrupt only this owned disposable fixture to prove startup and
            # migration refuse an unmanaged nonempty database without applying DDL.
            cursor.execute("DELETE FROM __EFMigrationsHistory")
        stale_schema = run_api(timeout=60)
        check("startup refuses missing EF migration history",
              refused_with(stale_schema, "Database has unapplied EF Core migrations"))
        refused_migration = run_api("--migrate-database", timeout=60)
        check("explicit migration refuses empty EF history on a nonempty database",
              refused_with(refused_migration, "Migration refused: EF migration history is empty"))
        with conn.cursor() as cursor:
            cursor.executemany(
                "INSERT INTO __EFMigrationsHistory(MigrationId,ProductVersion) VALUES(%s,%s)",
                expected_history,
            )
        current_migration = run_api("--migrate-database", timeout=300)
        if current_migration.returncode:
            raise RuntimeError(
                "EF migration failed after restoring the owned test history: "
                + current_migration.stderr.decode(errors="replace")[:1500]
            )
        conversion = run_api("--convert-file-blobs", timeout=120)
        conversion_lines = [line for line in conversion.stdout.decode(errors="replace").splitlines() if line.strip()]
        conversion_result = json.loads(conversion_lines[-1]) if conversion.returncode == 0 and conversion_lines else {}
        check("explicit file content conversion is a repeatable one-shot command",
              conversion_result.get("convertedFiles") == 0 and conversion_result.get("removedLegacyFiles") is None)
        misplaced_cleanup = run_api("--remove-legacy-content", timeout=60)
        check("legacy content cleanup is refused outside the conversion command",
              refused_with(misplaced_cleanup,
                           "--remove-legacy-content is only valid together with --convert-file-blobs"))
        if not TEST_HOST.is_file():
            raise RuntimeError("Build server_dotnet/TestHost/Yf.Api.TestHost.csproj before HTTP testing")
        test_dll = TEST_HOST
        if PUBLISHED:
            # Run the exact extracted production assembly and dependencies under
            # the loopback-only test host, in a disposable copy. Never modify ZIP.
            test_payload = Path(temp) / "test-payload"
            shutil.copytree(API, test_payload)
            shutil.copy2(TEST_HOST, test_payload / TEST_HOST.name)
            test_dll = test_payload / TEST_HOST.name
        verify_test_host_artifacts(API if PUBLISHED else DLL.parent, test_dll, use_api_runtime=bool(PUBLISHED))
        production_command = ["dotnet", str(DLL), "--urls", base]
        host_command = ["dotnet", str(test_dll)]
        if PUBLISHED:
            host_command = ["dotnet", "exec", "--depsfile", str(test_dll.parent / "Yf.Api.deps.json"),
                            "--runtimeconfig", str(test_dll.parent / "Yf.Api.runtimeconfig.json"), str(test_dll)]
        check("test host uses exact API assembly and managed runtime dependencies", True)
        api_log_path.parent.mkdir(parents=True, exist_ok=True)
        with open(api_log_path, "wb") as log:
            process = subprocess.Popen(production_command, cwd=content_root, env=env, stdout=log, stderr=log)
            stack.callback(stop_process, process)
            client = Client(base)
            try:
                wait_for_http_ready(
                    process,
                    lambda: client.call("GET", "/health") is not None,
                    base + "/health",
                )
            except (RuntimeError, TimeoutError) as error:
                raise RuntimeError(
                    f"Test API readiness failed: {error}; "
                    + api_log_path.read_text(errors="replace")[-1800:]
                ) from error
            check("production entry HTTP health and database connectivity", True)
            unavailable_captcha = client.call("GET", "/api/v1/auth/captcha", expected=401)
            check("production authentication API does not expose CAPTCHA anonymously",
                  unavailable_captcha["code"] == 40101)
            if PUBLISHED:
                page, _ = client.call("GET", "/", raw=True)
                login_page, _ = client.call("GET", "/login", raw=True)
                check("published frontend and SPA routing", b"<html" in page.lower() and page == login_page)
                client.call("GET", "/appsettings.json", expected=404, raw=True)
                client.call("GET", "/Yf.Api.dll", expected=404, raw=True)
                unknown = client.call("GET", "/api/unknown", expected=404)
                check("published config binaries and unknown API are not exposed", unknown["code"] == 40401)
            stop_process(process)
            process = subprocess.Popen(host_command, cwd=content_root, env=env, stdout=log, stderr=log)
            stack.callback(stop_process, process)
            client = Client(base)
            try:
                wait_for_http_ready(
                    process,
                    lambda: client.call("GET", "/health") is not None,
                    base + "/health",
                )
            except (RuntimeError, TimeoutError) as error:
                raise RuntimeError(
                    f"Loopback test host readiness failed: {error}; "
                    + api_log_path.read_text(errors="replace")[-1800:]
                ) from error
            client.call("GET", "/api/v1/project-groups", expected=401)
            check("anonymous API denied", True)
            run_anonymous_route_contracts(client, check)
            result = client.login("admin", initial)
            check("bootstrap direct login enforces password change without CAPTCHA", result["mustChangePassword"])
            client.call("GET", "/api/v1/project-groups", expected=403)
            client.call("PUT", "/api/v1/auth/password", {"oldPassword": initial, "newPassword": changed})
            client.call("GET", "/api/v1/auth/profile", expected=401)
            result = client.login("admin", changed)
            check("password change revokes old session and re-login works", not result["mustChangePassword"] and result["user"]["isSystemAdmin"])
            missing_captcha = client.call("GET", "/api/v1/auth/captcha", expected=404)
            check("authenticated authentication API has no CAPTCHA endpoint", missing_captcha["code"] == 40401)
            profile = client.call("GET", "/api/v1/auth/profile")
            check("profile has frontend permission/menu contract", bool(profile["permissions"]) and bool(profile["menus"]))
            _, cors_headers = client.call("OPTIONS", "/api/v1/projects", expected=204,
                headers={"Access-Control-Request-Method": "GET", "Access-Control-Request-Headers": "authorization"}, raw=True)
            check("routed API preflight keeps the configured credentialed origin",
                  cors_headers.get("Access-Control-Allow-Origin") == client.base
                  and cors_headers.get("Access-Control-Allow-Credentials") == "true")
            negotiation = client.call("POST", "/api/v1/collaboration/live/negotiate?negotiateVersion=1")
            check("SignalR negotiation remains authenticated after static pipeline routing",
                  any(item["transport"] == "WebSockets" for item in negotiation["availableTransports"]))
            if not FILES_ONLY:
                run_identity_checks(client, Client, conn, check)
                run_project_remediation_checks(client, Client, conn, check, storage)
                run_collaboration_checks(client, Client, conn, check)
                for path in ("/dashboard/summary", "/dashboard/pending-projects", "/departments", "/permissions", "/supplier-options", "/robot-parts?enabledOnly=true", "/admin/users", "/admin/roles", "/admin/suppliers", "/admin/user-role-options", "/admin/system/configs", "/admin/system/mail-status", "/admin/audit-logs"):
                    client.call("GET", "/api/v1" + path)
                    check("read contract " + path, True)
                configs = client.call("GET", "/api/v1/admin/system/configs")
                check("internal management lock hidden", all(x["key"] != "security.management_lock" for x in configs))
                client.call("PUT", "/api/v1/admin/system/configs", {"items": [{"key": "security.management_lock", "value": "x"}]}, expected=400)
            client.call("PUT", "/api/v1/admin/system/configs", {"items": [{"key": "upload.chunk_size", "value": "262144"}]})
            check("configuration validation and update", True)
            if not FILES_ONLY:
                run_system_checks(client, conn, check)
            supplier = client.call("POST", "/api/v1/admin/suppliers", {"name": ".NET 隔离供应商", "remark": "temporary"})
            sid = supplier["id"]
            _, project = _create_project_group(
                client, client, conn, sid, ".NET 隔离项目")
            pid = project["id"]
            # Starting requires an enabled supplier account that can submit.
            flow_password = "Yf9!" + secrets.token_urlsafe(9)
            changed_flow_password = "Yf9!" + secrets.token_urlsafe(9)
            flow_user = client.call("POST", f"/api/v1/admin/suppliers/{sid}/accounts", {"employeeNo": "workflow_supplier", "password": flow_password, "realName": "验收供应商", "email": "workflow@example.invalid"})
            client.call("PUT", f"/api/v1/projects/{pid}/status", {"status": "IN_PROGRESS"})
            check("supplier/project creation and project start", True)
            pdf = b"%PDF-1.4\n" + b"test data\n" * 40000 + b"%%EOF\n"
            # Internal (company-to-supplier) upload on a fresh subproject: opt in to the STEP prerequisite fixture.
            upload = init_upload(client, pid, "regression.pdf", pdf, ensure_step=True)
            session = upload["sessionId"]
            size = upload["chunkSize"]
            check("upload uses the updated chunk-size setting", size == 262144)
            for index in range(upload["totalChunks"]):
                put_chunk(client, session, index, pdf[index * size:(index + 1) * size])
            resumed = client.call("GET", f"/api/v1/uploads/{session}")
            check(
                "chunk upload and resume state",
                len(resumed["uploadedChunks"]) == upload["totalChunks"]
                and all(
                    item["index"] == index and len(item["sha256"]) == 64
                    for index, item in enumerate(resumed["uploadedChunks"])
                ),
            )
            submit_md5(client, session, pdf)
            merged = client.call("POST", f"/api/v1/uploads/{session}/merge")
            fid = merged["id"]
            downloaded, headers = client.call("GET", f"/api/v1/files/{fid}/download", raw=True)
            check("download bytes and SHA256", hashlib.sha256(downloaded).digest() == hashlib.sha256(pdf).digest())
            partial, headers = client.call("GET", f"/api/v1/files/{fid}/content", expected=206, headers={"Range": "bytes=0-7"}, raw=True)
            check("PDF content range preview", partial == pdf[:8] and headers.get("Content-Range") == f"bytes 0-7/{len(pdf)}")
            archive, _ = client.call("POST", "/api/v1/files/batch-download", {"ids": [fid]}, raw=True)
            with zipfile.ZipFile(io.BytesIO(archive)) as zipped:
                check("batch ZIP member integrity", zipped.testzip() is None and zipped.read(zipped.namelist()[0]) == pdf)
            run_file_checks(client, conn, check, pid, fid)
            run_native_download_checks(client, conn, check, fid, pdf)
            if FILES_ONLY:
                raise FileContractsComplete()
            run_background_copy_checks(client, conn, check, sid)
            run_upload_material_checks(client, conn, check, sid)
            run_recent_route_contracts(client, Client, conn, check)
            recovery_bytes = b"%PDF-1.4\nowned interrupted merge regression\n%%EOF\n"
            recovery = init_upload(client, pid, "recovery.pdf", recovery_bytes)
            recovery_id = recovery["sessionId"]
            put_chunk(client, recovery_id, 0, recovery_bytes)
            submit_md5(client, recovery_id, recovery_bytes)
            with conn.cursor() as cursor:
                cursor.execute("UPDATE upload_sessions SET status='MERGING' WHERE id=%s", (recovery_id,))
            recovered = client.call("POST", f"/api/v1/uploads/{recovery_id}/merge")
            recovered_data, _ = client.call("GET", f"/api/v1/files/{recovered['id']}/download", raw=True)
            check("abandoned merge with deferred MD5 completes on retry with exact bytes", recovered_data == recovery_bytes)
            message = client.call("POST", f"/api/v1/projects/{pid}/messages", {"content": "隔离接口测试留言"})
            mid = message["id"]
            client.call("POST", "/api/v1/messages/read", {"ids": [mid]})
            client.call("GET", f"/api/v1/messages/{mid}/reads")
            check("message creation/read tracking", True)
            for suffix in ("", "/summary", "/activities", "/files", "/messages"):
                client.call("GET", f"/api/v1/projects/{pid}" + suffix)
            check("project detail and collaboration read routes", True)
            supplier_client = Client(base)
            first_flow_login = supplier_client.login("workflow_supplier", flow_password)
            supplier_client.call("GET", "/api/v1/project-groups", expected=403)
            supplier_client.call("PUT", "/api/v1/auth/password", {"oldPassword": flow_password, "newPassword": changed_flow_password})
            supplier_client.call("GET", "/api/v1/auth/profile", expected=401)
            second_flow_login = supplier_client.login("workflow_supplier", changed_flow_password)
            check("workflow supplier changes initial password through API", first_flow_login["mustChangePassword"] and not second_flow_login["mustChangePassword"] and second_flow_login["user"]["id"] == flow_user["id"])
            supplier_groups = supplier_client.call("GET", "/api/v1/project-groups")
            check("supplier account sees its supplier enterprise groups",
                  any(item["id"] == project["projectGroupId"] for item in supplier_groups["list"]))
            first_submission = supplier_client.call("POST", f"/api/v1/projects/{pid}/submit", {})
            supplier_client.call("POST", f"/api/v1/projects/{pid}/withdraw", {
                "expectedSubmissionId": first_submission["latestSubmissionId"],
            })
            supplier_submission = supplier_client.call("POST", f"/api/v1/projects/{pid}/submit", {})
            client.call("POST", f"/api/v1/projects/{pid}/reject", {
                "reason": "回归测试驳回",
                "expectedSubmissionId": supplier_submission["latestSubmissionId"],
            })
            final_submission = supplier_client.call(
                "POST", f"/api/v1/projects/{pid}/submit", {"confirmSide": "COMPANY"})
            completed_project = client.call("POST", f"/api/v1/projects/{pid}/confirm", {
                "expectedSubmissionId": final_submission["latestSubmissionId"],
            })
            check(
                "supplier submit withdraw resubmit and internal acceptance workflow",
                first_submission["confirmSide"] == "COMPANY"
                and supplier_submission["confirmSide"] == "COMPANY"
                and final_submission["confirmSide"] == "COMPANY"
                and all(
                    isinstance(item["latestSubmissionId"], int)
                    for item in (first_submission, supplier_submission, final_submission)
                )
                and completed_project["status"] == "COMPLETED"
                and completed_project["latestSubmissionId"] is None,
            )
            client.call("DELETE", f"/api/v1/files/{fid}", expected=409)
            check("completed project file mutation denied", True)
            run_business_acceptance(client, Client, conn, check)
            run_workflow_acceptance(client, Client, conn, check)
            run_manual_supplier_role_checks(client, conn, check)
            process.terminate()
            process.wait(timeout=15)
            process = None
        print(f"PASS {len(checks)} checks; no production data or email used", flush=True)
        report.parent.mkdir(parents=True, exist_ok=True)
        report.write_text(json.dumps({"passed": len(checks), "checks": checks, "businessDatabaseTouched": False, "smtpUsed": False, "productionEntrySmoke": True, "httpSuiteHost": "loopback-only host using the same API factory and assembly"}, ensure_ascii=False, indent=2), encoding="utf-8")
except FileContractsComplete:
    print(f"PASS {len(checks)} focused file checks; no production data or email used", flush=True)
    report.parent.mkdir(parents=True, exist_ok=True)
    report.write_text(json.dumps({"passed": len(checks), "checks": checks, "businessDatabaseTouched": False, "smtpUsed": False, "productionEntrySmoke": True, "httpSuiteHost": "loopback-only host using the same API factory and assembly"}, ensure_ascii=False, indent=2), encoding="utf-8")
finally:
    if process is not None:
        process.terminate()
        try:
            process.wait(timeout=15)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait()
    if created:
        with conn.cursor() as cursor:
            cursor.execute(f"DROP DATABASE `{name}`")
        print("Owned isolated database removed; business database unchanged", flush=True)
    conn.close()
