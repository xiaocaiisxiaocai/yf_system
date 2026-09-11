"""Run the real ASP.NET API against an owned disposable MySQL database and storage.

Requires Python 3.11+, pymysql, and a built Yf.Api. No real email is sent.
Connection credentials stay in process memory; only test names/results are printed.
"""
import hashlib
import base64
import contextlib
import http.cookiejar
import io
import json
import os
from pathlib import Path
import secrets
import socket
import subprocess
import tempfile
import time
import tomllib
import urllib.error
import urllib.parse
import urllib.request
import uuid
import zipfile
import zlib
import pymysql
from test_identity_contracts import run_identity_checks
from test_file_contracts import run_file_checks
from test_system_contracts import run_system_checks

ROOT = Path(__file__).resolve().parents[2]
PUBLISHED = os.environ.get("YF_TEST_API_DIR")
API = Path(PUBLISHED).resolve() if PUBLISHED else ROOT / "server_dotnet/Yf.Api"
DLL = API / "Yf.Api.dll" if PUBLISHED else API / "bin/Debug/net10.0/Yf.Api.dll"
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
        challenge = self.call("GET", "/api/v1/auth/captcha")
        code = test_captcha_digits(challenge["svg"])
        result = self.call("POST", "/api/v1/auth/login", {"employeeNo": username, "password": password, "captchaId": challenge["captchaId"], "captchaCode": code})
        self.token = result["accessToken"]
        return result


def test_captcha_digits(data_url):
    """Decode this backend's generated test image; no authentication bypass is added to API."""
    png = base64.b64decode(data_url.split(",", 1)[1])
    pos, data = 8, b""
    while pos < len(png):
        length = int.from_bytes(png[pos:pos + 4], "big")
        if png[pos + 4:pos + 8] == b"IDAT":
            data += png[pos + 8:pos + 8 + length]
        pos += length + 12
    pixels = zlib.decompress(data)
    templates = [{0,1,2,3,4,5}, {1,2}, {0,1,6,4,3}, {0,1,6,2,3}, {5,6,1,2},
                 {0,5,6,2,3}, {0,5,6,4,2,3}, {0,1,2}, {0,1,2,3,4,5,6}, {0,1,2,3,5,6}]
    centers = [(9,1), (17,11), (17,30), (9,40), (1,30), (1,11), (9,21)]
    digits = []
    for i in range(6):
        active = set()
        for segment, (dx, dy) in enumerate(centers):
            x, y = 13 + 29 * i + dx, 10 + dy
            dark = sum(pixels[(y + oy) * 577 + 1 + (x + ox) * 3] < 65 for ox in (-1, 0, 1) for oy in (-1, 0, 1))
            if dark >= 4:
                active.add(segment)
        digits.append(str(templates.index(active)))
    return "".join(digits)


config = tomllib.loads((ROOT / "yf_server/config.local.toml").read_text(encoding="utf-8-sig"))
url = urllib.parse.urlsplit(config["database"]["url"])
if url.hostname not in ("127.0.0.1", "localhost", "::1"):
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
        initial = secrets.token_urlsafe(24)
        changed = secrets.token_urlsafe(24)
        env = os.environ.copy()
        env.pop("YF_CONFIG_PATH", None)
        env.update({"App__ConnectionString": f"Server={cs(url.hostname)};Port={url.port or 3306};Database={name};User ID={cs(user)};Password={cs(password)}",
                    "App__JwtSecret": secrets.token_urlsafe(48), "App__StorageRoot": str(storage),
                    "App__WebBaseUrl": base, "App__CookieSecure": "false", "App__WorkerEnabled": "false",
                    "App__Smtp__Host": "", "ASPNETCORE_URLS": base, "YF_BOOTSTRAP_PASSWORD": initial,
                    "Logging__LogLevel__Default": "Warning"})
        initialized = subprocess.run(["dotnet", str(DLL), "--initialize-database"], cwd=API, env=env, capture_output=True)
        if initialized.returncode:
            raise RuntimeError(".NET empty database initialization failed: " + initialized.stderr.decode(errors="replace")[:1500])
        check("standalone empty database initialization", True)
        refused = subprocess.run(["dotnet", str(DLL), "--initialize-database"], cwd=API, env=env, capture_output=True)
        check("initializer refuses nonempty database", refused.returncode != 0)
        del env["YF_BOOTSTRAP_PASSWORD"]
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
            check("HTTP health and database connectivity", True)
            if PUBLISHED:
                page, _ = client.call("GET", "/", raw=True)
                login_page, _ = client.call("GET", "/login", raw=True)
                check("published frontend and SPA routing", b"<html" in page.lower() and page == login_page)
                client.call("GET", "/appsettings.json", expected=404, raw=True)
                client.call("GET", "/Yf.Api.dll", expected=404, raw=True)
                unknown = client.call("GET", "/api/unknown", expected=404)
                check("published config binaries and unknown API are not exposed", unknown["code"] == 40401)
            client.call("GET", "/api/v1/projects", expected=401)
            check("anonymous API denied", True)
            result = client.login("admin", initial)
            check("bootstrap login enforces password change", result["mustChangePassword"])
            client.call("GET", "/api/v1/projects", expected=403)
            client.call("PUT", "/api/v1/auth/password", {"oldPassword": initial, "newPassword": changed})
            client.call("GET", "/api/v1/auth/profile", expected=401)
            result = client.login("admin", changed)
            check("password change revokes old session and re-login works", not result["mustChangePassword"] and result["user"]["isSystemAdmin"])
            profile = client.call("GET", "/api/v1/auth/profile")
            check("profile has frontend permission/menu contract", bool(profile["permissions"]) and bool(profile["menus"]))
            run_identity_checks(client, Client, conn, check)
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
            project = client.call("POST", "/api/v1/projects", {"name": ".NET 隔离项目", "description": "compatibility", "supplierId": sid})
            pid = project["id"]
            client.call("PUT", f"/api/v1/projects/{pid}/status", {"status": "IN_PROGRESS"})
            check("supplier/project creation and project start", True)
            pdf = b"%PDF-1.4\n" + b"test data\n" * 40000 + b"%%EOF\n"
            upload = client.call("POST", "/api/v1/uploads/init", {"projectId": pid, "fileName": "compatibility.pdf", "fileSize": len(pdf), "fileMd5": hashlib.md5(pdf).hexdigest()})
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
            message = client.call("POST", f"/api/v1/projects/{pid}/messages", {"content": "接口兼容测试留言"})
            mid = message["id"]
            client.call("POST", "/api/v1/messages/read", {"ids": [mid]})
            client.call("GET", f"/api/v1/messages/{mid}/reads")
            check("message creation/read tracking", True)
            for suffix in ("", "/summary", "/members", "/supplier-members", "/activities", "/files", "/messages"):
                client.call("GET", f"/api/v1/projects/{pid}" + suffix)
            check("project detail and collaboration read routes", True)
            flow_password = secrets.token_urlsafe(24)
            flow_user = client.call("POST", f"/api/v1/admin/suppliers/{sid}/accounts", {"employeeNo": "workflow_supplier", "password": flow_password, "realName": "验收供应商", "email": "workflow@example.invalid"})
            with conn.cursor() as cursor:
                cursor.execute("UPDATE users SET must_change_password=0 WHERE id=%s", (flow_user["id"],))
            supplier_client = Client(base)
            supplier_client.login("workflow_supplier", flow_password)
            client.call("POST", f"/api/v1/projects/{pid}/submit", {"confirmSide": "SUPPLIER"})
            client.call("POST", f"/api/v1/projects/{pid}/withdraw")
            client.call("POST", f"/api/v1/projects/{pid}/submit", {"confirmSide": "SUPPLIER"})
            supplier_client.call("POST", f"/api/v1/projects/{pid}/reject", {"reason": "回归测试驳回"})
            client.call("POST", f"/api/v1/projects/{pid}/submit", {"confirmSide": "SUPPLIER"})
            supplier_client.call("POST", f"/api/v1/projects/{pid}/confirm")
            check("project submit/withdraw/reject/confirm workflow", True)
            client.call("DELETE", f"/api/v1/files/{fid}", expected=409)
            check("completed project file mutation denied", True)
            rust_executable = os.environ.get("YF_TEST_RUST_EXE")
            if rust_executable:
                stop_process(process)
                process = None
                rust_config = Path(temp) / "rust-test.toml"
                rust_config.write_text(
                    f'[server]\naddr = "127.0.0.1:{port}"\n'
                    '[database]\nurl = ""\nauto_migrate = false\n'
                    f'[storage]\nroot = "{storage.as_posix()}"\n'
                    '[jwt]\nsecret = ""\naccess_ttl_minutes = 30\nrefresh_ttl_days = 7\ncookie_secure = false\n'
                    '[upload]\nmax_file_size = 2147483648\nchunk_size = 262144\n[smtp]\nhost = ""\n'
                    f'[web]\nbase_url = "{base}"\n', encoding="utf-8")
                rust_env = env.copy()
                rust_env.update({"YF_CONFIG": str(rust_config), "YF_DATABASE_URL": urllib.parse.urlunsplit((url.scheme, url.netloc, "/" + name, url.query, "")),
                                 "YF_JWT_SECRET": env["App__JwtSecret"], "YF_SMTP_HOST": "", "YF_WEB_BASE_URL": base})
                process = subprocess.Popen([str(Path(rust_executable).resolve())], cwd=Path(temp), env=rust_env, stdout=log, stderr=log)
                stack.callback(stop_process, process)
                for _ in range(100):
                    if process.poll() is not None:
                        raise RuntimeError("Isolated Rust compatibility process exited")
                    try:
                        client.call("GET", "/health")
                        break
                    except (OSError, AssertionError):
                        time.sleep(0.1)
                rust_profile = client.call("GET", "/api/v1/auth/profile")
                check("Rust accepts ASP.NET JWT and shared persisted session", rust_profile["user"]["employeeNo"] == "admin")
                refreshed = client.call("POST", "/api/v1/auth/refresh")
                client.token = refreshed["accessToken"]
                check("Rust rotates ASP.NET-issued refresh cookie", bool(client.token))
                stop_process(process)
                process = subprocess.Popen(["dotnet", str(DLL)], cwd=API, env=env, stdout=log, stderr=log)
                stack.callback(stop_process, process)
                for _ in range(100):
                    if process.poll() is not None:
                        raise RuntimeError("ASP.NET compatibility restart exited")
                    try:
                        client.call("GET", "/health")
                        break
                    except (OSError, AssertionError):
                        time.sleep(0.1)
                dotnet_profile = client.call("GET", "/api/v1/auth/profile")
                check("ASP.NET accepts Rust JWT and shared persisted session", dotnet_profile["user"]["employeeNo"] == "admin")
                refreshed = client.call("POST", "/api/v1/auth/refresh")
                client.token = refreshed["accessToken"]
                check("ASP.NET rotates Rust-issued refresh cookie", bool(client.token))
            process.terminate()
            process.wait(timeout=15)
            process = None
        print(f"PASS {len(checks)} checks; no production data or email used", flush=True)
        report = ROOT / (".runlogs/dotnet-published-results.json" if PUBLISHED else ".runlogs/dotnet-isolated-results.json")
        report.parent.mkdir(exist_ok=True)
        report.write_text(json.dumps({"passed": len(checks), "checks": checks, "businessDatabaseTouched": False, "smtpUsed": False}, ensure_ascii=False, indent=2), encoding="utf-8")
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
