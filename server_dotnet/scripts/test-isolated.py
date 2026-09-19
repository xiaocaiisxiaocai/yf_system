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
import sys
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
import zipfile
import pymysql
from test_host_artifacts import verify_test_host_artifacts
from test_identity_contracts import run_identity_checks
from test_file_contracts import run_file_checks
from test_role_fixtures import assert_admin_only_initialization, install_legacy_test_roles
from test_system_contracts import run_system_checks
from test_project_remediation import run_project_remediation_checks
from test_collaboration_contracts import run_collaboration_checks
from test_business_acceptance import _create_project_group, run_business_acceptance
from test_workflow_acceptance import run_workflow_acceptance
from test_manual_supplier_roles import run_manual_supplier_role_checks

ROOT = Path(__file__).resolve().parents[2]
PUBLISHED = os.environ.get("YF_TEST_API_DIR")
API = Path(PUBLISHED).resolve() if PUBLISHED else ROOT / "server_dotnet/Yf.Api"
DLL = API / "Yf.Api.dll" if PUBLISHED else API / "bin/Debug/net10.0/Yf.Api.dll"
TEST_HOST = ROOT / "server_dotnet/TestHost/bin/Debug/net10.0/Yf.Api.TestHost.dll"
checks = []
FILES_ONLY = sys.argv[1:] == ["--files-only"]
if sys.argv[1:] and not FILES_ONLY:
    raise SystemExit("Usage: test-isolated.py [--files-only]")


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


def assign_admin_project_section(client, connection, admin_id):
    """Give the disposable admin fixture a valid project-owner section."""
    suffix = secrets.token_hex(5)
    division = client.call("POST", "/api/v1/admin/departments", {
        "name": "隔离测试事业部-" + suffix,
        "parentId": None,
        "sortNo": 130,
    })
    department = client.call("POST", "/api/v1/admin/departments", {
        "name": "隔离测试部门-" + suffix,
        "parentId": division["id"],
        "sortNo": 131,
    })
    section = client.call("POST", "/api/v1/admin/departments", {
        "name": "隔离测试课别-" + suffix,
        "parentId": department["id"],
        "sortNo": 132,
    })
    with connection.cursor() as cursor:
        cursor.execute("UPDATE users SET department_id=%s WHERE id=%s", (section["id"], admin_id))
    return admin_id


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
        env = {
            key: value for key, value in os.environ.items()
            if not key.lower().startswith(("app__", "app:"))
            and key.upper() not in {"YF_CONFIG_PATH", "YF_BOOTSTRAP_PASSWORD"}
        }
        env.update({"App__ConnectionString": f"Server={cs(url.hostname)};Port={url.port or 3306};Database={name};User ID={cs(user)};Password={cs(password)}",
                    "App__JwtSecret": secrets.token_urlsafe(48), "App__StorageRoot": str(storage),
                    "App__WebBaseUrl": base, "App__CookieSecure": "false", "App__WorkerEnabled": "false",
                    "App__Smtp__Host": "", "ASPNETCORE_URLS": base, "URLS": base, "YF_BOOTSTRAP_PASSWORD": initial,
                    "Logging__LogLevel__Default": "Warning"})
        initialized = subprocess.run(["dotnet", str(DLL), "--initialize-database"], cwd=API, env=env, capture_output=True)
        if initialized.returncode:
            raise RuntimeError(".NET empty database initialization failed: " + initialized.stderr.decode(errors="replace")[:1500])
        check("standalone empty database initialization", True)
        assert_admin_only_initialization(conn)
        check("empty initialization creates only the admin user and system administrator role", True)
        refused = subprocess.run(["dotnet", str(DLL), "--initialize-database"], cwd=API, env=env, capture_output=True)
        check("initializer refuses nonempty database", refused.returncode != 0)
        del env["YF_BOOTSTRAP_PASSWORD"]
        # Legacy role fixtures are test-only; production initialization remains admin-only.
        install_legacy_test_roles(conn)
        with conn.cursor() as cursor:
            cursor.execute("SELECT id,password_hash FROM users WHERE employee_no='admin'")
            admin_user_id, preserved_hash = cursor.fetchone()
            cursor.execute(
                "SELECT MigrationId,ProductVersion FROM __EFMigrationsHistory ORDER BY MigrationId"
            )
            expected_history = cursor.fetchall()
            cursor.execute(
                "SELECT COUNT(*) FROM project_dictionaries "
                "WHERE type='PRIORITY' AND name IN ('高','普通','低')"
            )
            check("fresh EF initialization seeds the default priorities", cursor.fetchone()[0] == 3)
        # Every EF migration in the source tree, in order: InitialCreate first, then later ones (e.g. AddOemPlatform).
        source_migrations = sorted(
            path.stem for path in (Path(__file__).resolve().parents[1] / "Yf.Api/Infrastructure/Migrations").glob("*.cs")
            if path.stem[:14].isdigit() and path.stem[14:15] == "_" and "." not in path.stem)
        check("fresh EF initialization records every source migration in order, starting with InitialCreate",
              bool(source_migrations) and source_migrations[0].endswith("_InitialCreate")
              and [row[0] for row in expected_history] == source_migrations)

        for _ in range(2):
            migration = subprocess.run(
                ["dotnet", str(DLL), "--migrate-database"],
                cwd=API, env=env, capture_output=True,
            )
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
        stale_schema = subprocess.run(
            ["dotnet", str(DLL)], cwd=API, env=env, capture_output=True, timeout=20,
        )
        check("startup refuses missing EF migration history", stale_schema.returncode != 0)
        refused_migration = subprocess.run(
            ["dotnet", str(DLL), "--migrate-database"],
            cwd=API, env=env, capture_output=True, timeout=20,
        )
        check("explicit migration refuses empty EF history on a nonempty database",
              refused_migration.returncode != 0)
        with conn.cursor() as cursor:
            cursor.executemany(
                "INSERT INTO __EFMigrationsHistory(MigrationId,ProductVersion) VALUES(%s,%s)",
                expected_history,
            )
        current_migration = subprocess.run(
            ["dotnet", str(DLL), "--migrate-database"],
            cwd=API, env=env, capture_output=True,
        )
        if current_migration.returncode:
            raise RuntimeError(
                "EF migration failed after restoring the owned test history: "
                + current_migration.stderr.decode(errors="replace")[:1500]
            )
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
        verify_test_host_artifacts(API if PUBLISHED else DLL.parent, test_dll)
        check("test host uses exact API assembly and managed runtime dependencies", True)
        api_log_path = ROOT / ".runlogs/ef-final/test-isolated-api.log"
        api_log_path.parent.mkdir(parents=True, exist_ok=True)
        with open(api_log_path, "wb") as log:
            process = subprocess.Popen(["dotnet", str(DLL)], cwd=API, env=env, stdout=log, stderr=log)
            stack.callback(stop_process, process)
            client = Client(base)
            for _ in range(100):
                if process.poll() is not None:
                    raise RuntimeError("Test API exited: " + api_log_path.read_text(errors="replace")[-1800:])
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
            client.call("GET", "/api/v1/project-groups", expected=401)
            check("anonymous API denied", True)
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
            if not FILES_ONLY:
                run_identity_checks(client, Client, conn, check)
                run_project_remediation_checks(client, Client, conn, check)
                run_collaboration_checks(client, Client, conn, check)
                for path in ("/dashboard/summary", "/dashboard/pending-projects", "/departments", "/permissions", "/supplier-options", "/project-owner-options", "/admin/users", "/admin/roles", "/admin/suppliers", "/admin/user-role-options", "/admin/system/configs", "/admin/system/mail-status", "/admin/audit-logs"):
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
            owner_id = assign_admin_project_section(client, conn, admin_user_id)
            _, project = _create_project_group(
                client, conn, sid, owner_id, ".NET 隔离项目")
            pid = project["id"]
            client.call("PUT", f"/api/v1/projects/{pid}/status", {"status": "IN_PROGRESS"})
            check("supplier/project creation and project start", True)
            pdf = b"%PDF-1.4\n" + b"test data\n" * 40000 + b"%%EOF\n"
            upload = client.call("POST", "/api/v1/uploads/init", {"projectId": pid, "fileName": "regression.pdf", "fileSize": len(pdf), "fileMd5": hashlib.md5(pdf).hexdigest()})
            session = upload["sessionId"]
            size = upload["chunkSize"]
            check("upload uses the updated chunk-size setting", size == 262144)
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
            if FILES_ONLY:
                raise FileContractsComplete()
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
            for suffix in ("", "/summary", "/activities", "/files", "/messages"):
                client.call("GET", f"/api/v1/projects/{pid}" + suffix)
            check("project detail and collaboration read routes", True)
            flow_password = "Yf9!" + secrets.token_urlsafe(9)
            changed_flow_password = "Yf9!" + secrets.token_urlsafe(9)
            flow_user = client.call("POST", f"/api/v1/admin/suppliers/{sid}/accounts", {"employeeNo": "workflow_supplier", "password": flow_password, "realName": "验收供应商", "email": "workflow@example.invalid"})
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
        report = ROOT / (".runlogs/dotnet-published-results.json" if PUBLISHED else ".runlogs/dotnet-isolated-results.json")
        report.parent.mkdir(exist_ok=True)
        report.write_text(json.dumps({"passed": len(checks), "checks": checks, "businessDatabaseTouched": False, "smtpUsed": False, "productionEntrySmoke": True, "httpSuiteHost": "loopback-only host using the same API factory and assembly"}, ensure_ascii=False, indent=2), encoding="utf-8")
except FileContractsComplete:
    print(f"PASS {len(checks)} focused file checks; no production data or email used", flush=True)
    report = ROOT / ".runlogs/dotnet-file-results.json"
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
