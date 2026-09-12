"""Run the real ASP.NET API against an owned disposable MySQL database and storage.

Requires Python 3.11+, pymysql, and a built Yf.Api. No real email is sent.
Connection credentials stay in process memory; only test names/results are printed.
"""
import hashlib
import contextlib
import http.cookiejar
import io
import json
import os
from pathlib import Path
import secrets
import socket
import shutil
import subprocess
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
import zipfile
import pymysql
from test_identity_contracts import run_identity_checks
from test_file_contracts import run_file_checks
from test_system_contracts import run_system_checks
from test_project_remediation import run_project_remediation_checks
from test_business_acceptance import run_business_acceptance
from test_workflow_acceptance import run_workflow_acceptance

ROOT = Path(__file__).resolve().parents[2]
PUBLISHED = os.environ.get("YF_TEST_API_DIR")
API = Path(PUBLISHED).resolve() if PUBLISHED else ROOT / "server_dotnet/Yf.Api"
DLL = API / "Yf.Api.dll" if PUBLISHED else API / "bin/Debug/net10.0/Yf.Api.dll"
TEST_HOST = ROOT / "server_dotnet/TestHost/bin/Debug/net10.0/Yf.Api.TestHost.dll"
checks = []


def check(name, condition):
    if not condition:
        raise AssertionError(name)
    checks.append(name)
    print("PASS " + name, flush=True)


class Client:
    def __init__(self, base):
        self.base = base
        self.cookies = http.cookiejar.CookieJar()
        self.opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(self.cookies))
        self.token = None

    def call(self, method, path, body=None, expected=200, headers=None, raw=False):
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


config_url = os.environ.get("YF_TEST_DATABASE_URL")
if not config_url:
    raise SystemExit("Set process-scoped YF_TEST_DATABASE_URL to a local MySQL test-administration URL; no project configuration is discovered.")
url = urllib.parse.urlsplit(config_url)
if url.scheme != "mysql" or url.hostname not in ("127.0.0.1", "localhost", "::1"):
    raise SystemExit("Isolated testing only allows local MySQL")
name = "yf_test_dotnet_" + uuid.uuid4().hex
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


try:
    with conn.cursor() as cursor:
        cursor.execute(f"CREATE DATABASE `{name}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci")
        created = True
    conn.select_db(name)
    with tempfile.TemporaryDirectory(prefix="yf_dotnet_test_") as temp, contextlib.ExitStack() as stack:
        storage = Path(temp) / "storage"
        storage.mkdir()
        with socket.socket() as listener:
            listener.bind(("127.0.0.1", 0))
            port = listener.getsockname()[1]
        base = f"http://127.0.0.1:{port}"
        initial = "Yf9!" + secrets.token_urlsafe(9)
        changed = "Yf9!" + secrets.token_urlsafe(9)
        env = os.environ.copy()
        env.pop("YF_CONFIG_PATH", None)
        env.update({"App__ConnectionString": f"Server={cs(url.hostname)};Port={url.port or 3306};Database={name};User ID={cs(user)};Password={cs(password)}",
                    "App__JwtSecret": secrets.token_urlsafe(48), "App__StorageRoot": str(storage),
                    "App__WebBaseUrl": base, "App__CookieSecure": "false", "App__WorkerEnabled": "false",
                    "App__Smtp__Host": "", "ASPNETCORE_URLS": base, "URLS": base, "YF_BOOTSTRAP_PASSWORD": initial,
                    "Logging__LogLevel__Default": "Warning"})
        initialized = subprocess.run(["dotnet", str(DLL), "--initialize-database"], cwd=API, env=env, capture_output=True)
        if initialized.returncode:
            raise RuntimeError(".NET empty database initialization failed: " + initialized.stderr.decode(errors="replace")[:1500])
        check("standalone empty database initialization", True)
        refused = subprocess.run(["dotnet", str(DLL), "--initialize-database"], cwd=API, env=env, capture_output=True)
        check("initializer refuses nonempty database", refused.returncode != 0)
        del env["YF_BOOTSTRAP_PASSWORD"]
        # Downgrade only our empty isolated fixture to exercise adoption and the
        # restartable 16 -> 17 upgrade without invoking any other backend.
        with conn.cursor() as cursor:
            cursor.execute("SELECT password_hash FROM users WHERE employee_no='admin'")
            preserved_hash = cursor.fetchone()[0]
            cursor.execute("DROP TABLE yf_schema_migrations")
            cursor.execute("DELETE FROM seaql_migrations WHERE version='m20260911_000017_auth_session_families'")
            cursor.execute("ALTER TABLE refresh_tokens DROP INDEX idx_refresh_tokens_session_state, DROP COLUMN session_id")
            cursor.execute("INSERT INTO refresh_tokens(user_id,token_hash,expires_at,revoked) SELECT id,%s,DATE_ADD(UTC_TIMESTAMP(),INTERVAL 1 DAY),0 FROM users WHERE employee_no='admin'", (secrets.token_hex(32),))
            legacy_token_id = cursor.lastrowid
            cursor.execute("INSERT IGNORE INTO role_permissions(role_id,permission_id) SELECT r.id,p.id FROM roles r CROSS JOIN permissions p WHERE r.name='供应商人员' AND p.code='user:manage'")
        for attempt in range(2):
            migration = subprocess.run(["dotnet", str(DLL), "--migrate-database"], cwd=API, env=env, capture_output=True)
            if migration.returncode:
                raise RuntimeError(".NET migration failed: " + migration.stderr.decode(errors="replace")[:1500])
        with conn.cursor() as cursor:
            cursor.execute("SELECT password_hash FROM users WHERE employee_no='admin'")
            check(".NET migration from baseline 16 is repeatable and preserves users", cursor.fetchone()[0] == preserved_hash)
            cursor.execute("SELECT COUNT(*) FROM yf_schema_migrations")
            check(".NET owns schema version history", cursor.fetchone()[0] == 1)
            cursor.execute("SELECT session_id FROM refresh_tokens WHERE id=%s", (legacy_token_id,))
            check("legacy refresh rows get persisted session family", cursor.fetchone()[0] == format(legacy_token_id, 'x').zfill(36))
            cursor.execute("SELECT COUNT(*) FROM role_permissions rp JOIN roles r ON r.id=rp.role_id JOIN permissions p ON p.id=rp.permission_id WHERE r.name='供应商人员' AND p.code='user:manage'")
            check("migration removes preexisting supplier management grants", cursor.fetchone()[0] == 0)
            cursor.execute("SELECT checksum FROM yf_schema_migrations WHERE version=1")
            checksum = cursor.fetchone()[0]
            cursor.execute("UPDATE yf_schema_migrations SET checksum=%s", ('0' * 64,))
        tampered = subprocess.run(["dotnet", str(DLL)], cwd=API, env=env, capture_output=True, timeout=20)
        check("startup rejects modified migration history", tampered.returncode != 0)
        with conn.cursor() as cursor:
            cursor.execute("UPDATE yf_schema_migrations SET checksum=%s", (checksum,))
        if not TEST_HOST.is_file():
            raise RuntimeError("Build server_dotnet/TestHost/Yf.Api.TestHost.csproj before HTTP testing")
        test_dll = TEST_HOST
        if PUBLISHED:
            # Run the exact extracted production assembly and dependencies under
            # the loopback-only test host, in a disposable copy. Never modify ZIP.
            test_payload = Path(temp) / "test-payload"
            shutil.copytree(API, test_payload)
            for suffix in (".dll", ".deps.json", ".runtimeconfig.json"):
                source = TEST_HOST.parent / ("Yf.Api.TestHost" + suffix)
                shutil.copy2(source, test_payload / source.name)
            test_dll = test_payload / TEST_HOST.name
            check("test host uses exact published API assembly", hashlib.sha256((test_payload / "Yf.Api.dll").read_bytes()).digest() == hashlib.sha256(DLL.read_bytes()).digest())
        with open(Path(temp) / "api.log", "wb") as log:
            process = subprocess.Popen(["dotnet", str(DLL)], cwd=API, env=env, stdout=log, stderr=log)
            stack.callback(stop_process, process)
            client = Client(base)
            for _ in range(100):
                if process.poll() is not None:
                    raise RuntimeError("Test API exited: " + (Path(temp) / "api.log").read_text(errors="replace")[-1800:])
                try:
                    client.call("GET", "/health")
                    break
                except (OSError, AssertionError):
                    time.sleep(0.1)
            else:
                raise RuntimeError("Test API health timeout")
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
            process = subprocess.Popen(["dotnet", str(test_dll)], cwd=API, env=env, stdout=log, stderr=log)
            stack.callback(stop_process, process)
            client = Client(base)
            for _ in range(100):
                if process.poll() is not None:
                    raise RuntimeError("Loopback test host exited: " + (Path(temp) / "api.log").read_text(errors="replace")[-1800:])
                try:
                    client.call("GET", "/health")
                    break
                except (OSError, AssertionError):
                    time.sleep(0.1)
            else:
                raise RuntimeError("Loopback test host health timeout")
            client.call("GET", "/api/v1/projects", expected=401)
            check("anonymous API denied", True)
            result = client.login("admin", initial)
            check("bootstrap direct login enforces password change without CAPTCHA", result["mustChangePassword"])
            client.call("GET", "/api/v1/projects", expected=403)
            client.call("PUT", "/api/v1/auth/password", {"oldPassword": initial, "newPassword": changed})
            client.call("GET", "/api/v1/auth/profile", expected=401)
            result = client.login("admin", changed)
            check("password change revokes old session and re-login works", not result["mustChangePassword"] and result["user"]["isSystemAdmin"])
            missing_captcha = client.call("GET", "/api/v1/auth/captcha", expected=404)
            check("authenticated authentication API has no CAPTCHA endpoint", missing_captcha["code"] == 40401)
            profile = client.call("GET", "/api/v1/auth/profile")
            check("profile has frontend permission/menu contract", bool(profile["permissions"]) and bool(profile["menus"]))
            run_identity_checks(client, Client, conn, check)
            run_project_remediation_checks(client, Client, conn, check)
            for path in ("/dashboard/summary", "/dashboard/pending-projects", "/departments", "/permissions", "/supplier-options", "/internal-user-options", "/admin/users", "/admin/roles", "/admin/suppliers", "/admin/user-role-options", "/admin/system/configs", "/admin/system/storage", "/admin/system/mail-status", "/admin/audit-logs"):
                client.call("GET", "/api/v1" + path)
                check("read contract " + path, True)
            configs = client.call("GET", "/api/v1/admin/system/configs")
            check("internal management lock hidden", all(x["key"] != "security.management_lock" for x in configs))
            client.call("PUT", "/api/v1/admin/system/configs", {"items": [{"key": "security.management_lock", "value": "x"}]}, expected=400)
            client.call("PUT", "/api/v1/admin/system/configs", {"items": [{"key": "upload.chunk_size", "value": "262144"}]})
            check("configuration validation and update", True)
            run_system_checks(client, conn, check)
            supplier = client.call("POST", "/api/v1/admin/suppliers", {"name": ".NET 隔离供应商", "remark": "temporary"})
            sid = supplier["id"]
            project = client.call("POST", "/api/v1/projects", {"name": ".NET 隔离项目", "description": "isolated regression", "supplierId": sid})
            pid = project["id"]
            client.call("PUT", f"/api/v1/projects/{pid}/status", {"status": "IN_PROGRESS"})
            check("supplier/project creation and project start", True)
            pdf = b"%PDF-1.4\n" + b"test data\n" * 40000 + b"%%EOF\n"
            upload = client.call("POST", "/api/v1/uploads/init", {"projectId": pid, "fileName": "regression.pdf", "fileSize": len(pdf), "fileMd5": hashlib.md5(pdf).hexdigest()})
            session = upload["sessionId"]
            size = upload["chunkSize"]
            for index in range(upload["totalChunks"]):
                client.call("PUT", f"/api/v1/uploads/{session}/chunks/{index}", pdf[index * size:(index + 1) * size], headers={"Content-Type": "application/octet-stream"})
            resumed = client.call("GET", f"/api/v1/uploads/{session}")
            check("chunk upload and resume state", len(resumed["uploadedChunks"]) == upload["totalChunks"])
            merged = client.call("POST", f"/api/v1/uploads/{session}/merge")
            fid = merged.get("id", merged.get("fileId"))
            if fid is None:
                raise AssertionError("merge response missing file identifier")
            downloaded, headers = client.call("GET", f"/api/v1/files/{fid}/download", raw=True)
            check("download bytes and SHA256", hashlib.sha256(downloaded).digest() == hashlib.sha256(pdf).digest())
            partial, headers = client.call("GET", f"/api/v1/files/{fid}/content", expected=206, headers={"Range": "bytes=0-7"}, raw=True)
            check("PDF content range preview", partial == pdf[:8] and headers.get("Content-Range") == f"bytes 0-7/{len(pdf)}")
            archive, _ = client.call("POST", "/api/v1/files/batch-download", {"ids": [fid]}, raw=True)
            with zipfile.ZipFile(io.BytesIO(archive)) as zipped:
                check("batch ZIP member integrity", zipped.testzip() is None and zipped.read(zipped.namelist()[0]) == pdf)
            run_file_checks(client, conn, check, pid, fid)
            recovery_bytes = b"%PDF-1.4\nowned interrupted merge regression\n%%EOF\n"
            recovery = client.call("POST", "/api/v1/uploads/init", {"projectId": pid, "fileName": "recovery.pdf", "fileSize": len(recovery_bytes)})
            recovery_id = recovery["sessionId"]
            client.call("PUT", f"/api/v1/uploads/{recovery_id}/chunks/0", recovery_bytes, headers={"Content-Type": "application/octet-stream"})
            with conn.cursor() as cursor:
                cursor.execute("UPDATE upload_sessions SET status='MERGING' WHERE id=%s", (recovery_id,))
            recovered = client.call("POST", f"/api/v1/uploads/{recovery_id}/merge")
            recovered_data, _ = client.call("GET", f"/api/v1/files/{recovered['id']}/download", raw=True)
            check("abandoned merge without MD5 completes on retry with exact bytes", recovered_data == recovery_bytes)
            message = client.call("POST", f"/api/v1/projects/{pid}/messages", {"content": "隔离接口测试留言"})
            mid = message["id"]
            client.call("POST", "/api/v1/messages/read", {"ids": [mid]})
            client.call("GET", f"/api/v1/messages/{mid}/reads")
            check("message creation/read tracking", True)
            for suffix in ("", "/summary", "/members", "/supplier-members", "/activities", "/files", "/messages"):
                client.call("GET", f"/api/v1/projects/{pid}" + suffix)
            check("project detail and collaboration read routes", True)
            flow_password = "Yf9!" + secrets.token_urlsafe(9)
            changed_flow_password = "Yf9!" + secrets.token_urlsafe(9)
            flow_user = client.call("POST", f"/api/v1/admin/suppliers/{sid}/accounts", {"employeeNo": "workflow_supplier", "password": flow_password, "realName": "验收供应商", "email": "workflow@example.invalid"})
            supplier_client = Client(base)
            first_flow_login = supplier_client.login("workflow_supplier", flow_password)
            supplier_client.call("GET", "/api/v1/projects", expected=403)
            supplier_client.call("PUT", "/api/v1/auth/password", {"oldPassword": flow_password, "newPassword": changed_flow_password})
            supplier_client.call("GET", "/api/v1/auth/profile", expected=401)
            second_flow_login = supplier_client.login("workflow_supplier", changed_flow_password)
            check("workflow supplier changes initial password through API", first_flow_login["mustChangePassword"] and not second_flow_login["mustChangePassword"] and second_flow_login["user"]["id"] == flow_user["id"])
            client.call("POST", f"/api/v1/projects/{pid}/submit", {"confirmSide": "SUPPLIER"})
            client.call("POST", f"/api/v1/projects/{pid}/withdraw")
            client.call("POST", f"/api/v1/projects/{pid}/submit", {"confirmSide": "SUPPLIER"})
            supplier_client.call("POST", f"/api/v1/projects/{pid}/reject", {"reason": "回归测试驳回"})
            client.call("POST", f"/api/v1/projects/{pid}/submit", {"confirmSide": "SUPPLIER"})
            supplier_client.call("POST", f"/api/v1/projects/{pid}/confirm")
            check("project submit/withdraw/reject/confirm workflow", True)
            client.call("DELETE", f"/api/v1/files/{fid}", expected=409)
            check("completed project file mutation denied", True)
            run_business_acceptance(client, Client, conn, check)
            run_workflow_acceptance(client, Client, conn, check)
            process.terminate()
            process.wait(timeout=15)
            process = None
        print(f"PASS {len(checks)} checks; no production data or email used", flush=True)
        report = ROOT / (".runlogs/dotnet-published-results.json" if PUBLISHED else ".runlogs/dotnet-isolated-results.json")
        report.parent.mkdir(exist_ok=True)
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
