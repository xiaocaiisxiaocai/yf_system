"""Exercise project SignalR delivery against an owned disposable API/database.

Required environment:
  YF_TEST_DATABASE_URL  local MySQL administration URL (mysql://...)
  YF_TEST_API_DIR       directory containing a previously built Yf.Api.dll

The official JavaScript SignalR 10.0.11 client receives credentials only over
stdin. This script does not read project configuration, deliver email, expose
credentials in process arguments, or print fixture secrets.
"""

import contextlib
import http.cookiejar
import json
import os
from pathlib import Path
import queue
import secrets
import socket
import subprocess
import sys
import tempfile
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid

import pymysql


class CheckFailure(AssertionError):
    pass


class Client:
    def __init__(self, base):
        self.base = base
        self.cookies = http.cookiejar.CookieJar()
        self.opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(self.cookies))
        self.token = None

    def call(self, method, path, body=None, expected=200, use_auth=True, origin=None):
        headers = {"Origin": origin or self.base}
        if use_auth and self.token:
            headers["Authorization"] = "Bearer " + self.token
        if body is not None:
            body = json.dumps(body).encode("utf-8")
            headers["Content-Type"] = "application/json"
        request = urllib.request.Request(self.base + path, data=body, headers=headers, method=method)
        try:
            response = self.opener.open(request, timeout=30)
        except urllib.error.HTTPError as error:
            response = error
        data = response.read()
        if response.status != expected:
            raise CheckFailure(f"{method} {path.split('?', 1)[0]} returned HTTP {response.status}")
        return json.loads(data) if data else None

    def login(self, employee_no, password):
        result = self.call("POST", "/api/v1/auth/login", {
            "employeeNo": employee_no,
            "password": password,
        })
        self.token = result["accessToken"]
        return result


class SignalRProbe:
    def __init__(self, script):
        self.process = subprocess.Popen(
            ["node", str(script)],
            cwd=script.parent,
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL,
            text=True,
            encoding="utf-8",
            bufsize=1,
        )
        self.responses = queue.Queue()
        self.sequence = 0
        self.reader = threading.Thread(target=self._read, daemon=True)
        self.reader.start()

    def _read(self):
        try:
            for line in self.process.stdout:
                try:
                    self.responses.put(json.loads(line))
                except json.JSONDecodeError:
                    self.responses.put(None)
        finally:
            self.responses.put(None)

    def command(self, action, timeout=10, **values):
        if self.process.poll() is not None or self.process.stdin is None:
            raise CheckFailure("SignalR probe exited")
        self.sequence += 1
        command_id = self.sequence
        command = {"id": command_id, "action": action, **values}
        self.process.stdin.write(json.dumps(command, separators=(",", ":")) + "\n")
        self.process.stdin.flush()
        try:
            response = self.responses.get(timeout=timeout)
        except queue.Empty as error:
            raise CheckFailure("SignalR probe response timeout") from error
        if response is None or response.get("id") != command_id or not response.get("ok"):
            raise CheckFailure("SignalR probe command failed")
        return response["result"]

    def close(self):
        if self.process.poll() is not None:
            return
        try:
            self.command("shutdown", timeout=5)
        except (BrokenPipeError, CheckFailure, OSError):
            pass
        if self.process.stdin:
            try:
                self.process.stdin.close()
            except OSError:
                pass
        try:
            self.process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            self.process.terminate()
            try:
                self.process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                self.process.kill()
                self.process.wait(timeout=5)


def _quoted_connection_value(value):
    return '"' + str(value).replace('"', '""') + '"'


def _stop_process(process):
    if process is None or process.poll() is not None:
        return
    process.terminate()
    try:
        process.wait(timeout=15)
    except subprocess.TimeoutExpired:
        process.kill()
        process.wait(timeout=15)


def _reserve_port():
    with socket.socket() as listener:
        listener.bind(("127.0.0.1", 0))
        return listener.getsockname()[1]


def _create_role(admin, permission_ids, codes, label):
    missing = set(codes) - permission_ids.keys()
    if missing:
        raise CheckFailure("required permission missing from initialized database")
    role = admin.call("POST", "/api/v1/admin/roles", {
        "name": label + "-" + secrets.token_hex(5),
        "description": "owned isolated SignalR fixture",
    })
    admin.call("PUT", f"/api/v1/admin/roles/{role['id']}/permissions", {
        "permissionIds": [permission_ids[code] for code in codes],
    })
    return role["id"]


def _insert_user(conn, employee_no, real_name, user_type, supplier_id, role_id, password_hash, creator_id):
    with conn.cursor() as cursor:
        cursor.execute(
            """
            INSERT INTO users
                (employee_no,password_hash,real_name,email,user_type,supplier_id,department_id,
                 status,must_change_password,failed_login_attempts,locked_until,last_login_at,
                 last_login_ip,created_by,created_at,updated_at)
            VALUES
                (%s,%s,%s,%s,%s,%s,NULL,'ACTIVE',0,0,NULL,NULL,NULL,%s,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6))
            """,
            (employee_no, password_hash, real_name, employee_no + "@example.invalid",
             user_type, supplier_id, creator_id),
        )
        user_id = cursor.lastrowid
        cursor.execute("INSERT INTO user_roles(user_id,role_id) VALUES(%s,%s)", (user_id, role_id))
    return user_id


def _mark(probe, names):
    return {name: probe.command("mark", name=name)["index"] for name in names}


def _wait_event(probe, name, since, project_id, kind, timeout_ms=1900):
    result = probe.command(
        "wait",
        timeout=max(5, timeout_ms / 1000 + 2),
        name=name,
        since=since,
        projectId=project_id,
        kind=kind,
        timeoutMs=timeout_ms,
    )
    return result["event"]


def _exact_event(event, project_id, kind):
    return (
        event is not None
        and event["payload"] == {"projectId": project_id, "kind": kind}
        and isinstance(event["receivedAt"], int)
    )


def run_signalr_checks(admin, supplier, outsider, conn, probe, check, shared_password):
    profile = admin.call("GET", "/api/v1/auth/profile")
    admin_id = profile["user"]["id"]
    permissions = {item["code"]: item["id"] for item in admin.call("GET", "/api/v1/permissions")}
    supplier_role = _create_role(admin, permissions, ["project:list", "message:create"], "SignalR供应商角色")
    outsider_role = _create_role(admin, permissions, ["project:list"], "SignalR外部项目角色")
    check("fixtures use explicit custom roles and permission codes",
          supplier_role > 1 and outsider_role > 1 and supplier_role != outsider_role)

    supplier_row = admin.call("POST", "/api/v1/admin/suppliers", {
        "name": "SignalR供应商-" + secrets.token_hex(5),
        "remark": "owned isolated SignalR fixture",
    })
    with conn.cursor() as cursor:
        cursor.execute("SELECT password_hash FROM users WHERE id=%s", (admin_id,))
        password_hash = cursor.fetchone()[0]
    supplier_employee = "sig_sup_" + secrets.token_hex(4)
    outsider_employee = "sig_out_" + secrets.token_hex(4)
    supplier_id = _insert_user(
        conn, supplier_employee, "SignalR供应商用户", "SUPPLIER", supplier_row["id"],
        supplier_role, password_hash, admin_id,
    )
    outsider_id = _insert_user(
        conn, outsider_employee, "SignalR项目外用户", "INTERNAL", None,
        outsider_role, password_hash, admin_id,
    )
    supplier.login(supplier_employee, shared_password)
    outsider.login(outsider_employee, shared_password)

    project = admin.call("POST", "/api/v1/projects", {
        "name": "SignalR项目-" + secrets.token_hex(5),
        "description": "owned isolated SignalR fixture",
        "supplierId": supplier_row["id"],
    })
    project_id = project["id"]
    admin.call("PUT", f"/api/v1/projects/{project_id}/members", {"userIds": [admin_id]})
    supplier.call("GET", f"/api/v1/projects/{project_id}")
    outsider.call("GET", f"/api/v1/projects/{project_id}", expected=403)
    check("project fixture separates supplier visibility from outside project user",
          supplier_id != outsider_id)

    query = urllib.parse.urlencode({"access_token": outsider.token})
    anonymous = Client(admin.base)
    anonymous.call("GET", "/api/v1/auth/profile?" + query, expected=401, use_auth=False)
    check("ordinary API endpoint rejects query access token", True)

    no_token = probe.command(
        "connect", name="no-token", baseUrl=admin.base,
        origin=admin.base, webSocketsOnly=True,
    )
    check("SignalR connection without token is rejected", not no_token["connected"])
    bad_origin = probe.command(
        "connect", name="bad-origin", baseUrl=admin.base, accessToken=outsider.token,
        origin="http://localhost:" + str(_reserve_port()), webSocketsOnly=True,
    )
    check("SignalR WebSocket connection with disallowed Origin is rejected", not bad_origin["connected"])

    for name, client in (("admin", admin), ("supplier", supplier), ("outsider", outsider)):
        connected = probe.command(
            "connect", name=name, baseUrl=admin.base, accessToken=client.token,
            origin=admin.base,
        )
        if not connected["connected"]:
            raise CheckFailure("authorized SignalR connection was rejected")
    check("three authenticated users establish real SignalR connections", True)

    marks = _mark(probe, ("admin", "supplier", "outsider"))
    activity_started = int(time.time() * 1000)
    admin.call("PUT", f"/api/v1/projects/{project_id}/status", {"status": "IN_PROGRESS"})
    admin_activity = _wait_event(probe, "admin", marks["admin"], project_id, "activity")
    supplier_activity = _wait_event(probe, "supplier", marks["supplier"], project_id, "activity")
    check("authorized project users receive exact activity event within two seconds",
          _exact_event(admin_activity, project_id, "activity")
          and _exact_event(supplier_activity, project_id, "activity")
          and 0 <= admin_activity["receivedAt"] - activity_started < 2000
          and 0 <= supplier_activity["receivedAt"] - activity_started < 2000)

    marks = _mark(probe, ("admin", "supplier"))
    message_started = int(time.time() * 1000)
    message = admin.call("POST", f"/api/v1/projects/{project_id}/messages", {
        "content": "SignalR即时留言-" + secrets.token_hex(6),
    })
    admin_message = _wait_event(probe, "admin", marks["admin"], project_id, "messages")
    supplier_message = _wait_event(probe, "supplier", marks["supplier"], project_id, "messages")
    check("message POST pushes exact event to both authorized users without polling",
          _exact_event(admin_message, project_id, "messages")
          and _exact_event(supplier_message, project_id, "messages")
          and 0 <= admin_message["receivedAt"] - message_started < 2000
          and 0 <= supplier_message["receivedAt"] - message_started < 2000)

    admin_mark = probe.command("mark", name="admin")["index"]
    receipt_started = int(time.time() * 1000)
    supplier.call("POST", "/api/v1/messages/read", {"ids": [message["id"]]})
    receipt = _wait_event(probe, "admin", admin_mark, project_id, "receipts")
    check("supplier read receipt reaches administrator without polling",
          _exact_event(receipt, project_id, "receipts")
          and 0 <= receipt["receivedAt"] - receipt_started < 2000)

    outsider_events = probe.command(
        "wait", timeout=5, name="outsider", since=marks.get("outsider", 0),
        projectId=project_id, timeoutMs=2000,
    )["event"]
    check("outside project user receives no project event", outsider_events is None)

    check("live connection is still open before logout",
          not probe.command("waitClosed", name="admin", timeoutMs=0)["closed"])
    admin_mark = probe.command("mark", name="admin")["index"]
    admin.call("POST", "/api/v1/auth/logout", {})
    supplier_mark = probe.command("mark", name="supplier")["index"]
    supplier.call("POST", f"/api/v1/projects/{project_id}/messages", {
        "content": "SignalR会话撤销验证-" + secrets.token_hex(5),
    })
    supplier_after_logout = _wait_event(
        probe, "supplier", supplier_mark, project_id, "messages")
    revoked_event = _wait_event(probe, "admin", admin_mark, project_id, "messages", 1900)
    closed = probe.command("waitClosed", timeout=5, name="admin", timeoutMs=1900)["closed"]
    check("logout revokes the old live connection before later delivery",
          _exact_event(supplier_after_logout, project_id, "messages")
          and revoked_event is None and closed)


def main():
    if not __debug__:
        raise SystemExit("Do not run with Python assertions disabled")
    admin_url = os.environ.get("YF_TEST_DATABASE_URL")
    api_value = os.environ.get("YF_TEST_API_DIR")
    if not admin_url or not api_value:
        raise SystemExit("Set YF_TEST_DATABASE_URL and YF_TEST_API_DIR for isolated SignalR testing")
    parsed = urllib.parse.urlsplit(admin_url)
    if parsed.scheme != "mysql" or parsed.hostname not in ("127.0.0.1", "localhost", "::1"):
        raise SystemExit("Isolated SignalR testing only allows local MySQL")
    api_dir = Path(api_value).resolve()
    dll = api_dir / "Yf.Api.dll"
    node_script = Path(__file__).resolve().with_name("test_signalr_client.cjs")
    signalr_package = Path(__file__).resolve().parents[2] / "web" / "node_modules" / "@microsoft" / "signalr" / "package.json"
    if not dll.is_file():
        raise SystemExit("YF_TEST_API_DIR does not contain Yf.Api.dll")
    if not node_script.is_file() or not signalr_package.is_file():
        raise SystemExit("SignalR Node probe or web SignalR dependency is missing")
    if json.loads(signalr_package.read_text(encoding="utf-8"))["version"] != "10.0.11":
        raise SystemExit("web SignalR dependency must be version 10.0.11")

    db_name = "yf_test_signalr_" + uuid.uuid4().hex
    db_user = urllib.parse.unquote(parsed.username or "")
    db_password = urllib.parse.unquote(parsed.password or "")
    conn = pymysql.connect(
        host=parsed.hostname,
        port=parsed.port or 3306,
        user=db_user,
        password=db_password,
        autocommit=True,
    )
    created = False
    process = None
    probe = None
    passed = []

    def check(name, condition):
        if not condition:
            raise CheckFailure(name)
        passed.append(name)
        print("PASS " + name, flush=True)

    try:
        with conn.cursor() as cursor:
            cursor.execute(f"CREATE DATABASE `{db_name}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci")
            created = True
        conn.select_db(db_name)
        with tempfile.TemporaryDirectory(prefix="yf_signalr_test_") as temp, contextlib.ExitStack() as stack:
            storage = Path(temp) / "storage"
            storage.mkdir()
            port = _reserve_port()
            base = f"http://127.0.0.1:{port}"
            initial_password = "Yf9!" + secrets.token_hex(6)
            changed_password = "Zq8!" + secrets.token_hex(6)
            env = {
                key: value for key, value in os.environ.items()
                if not key.lower().startswith("app__")
                and key.upper() not in {"YF_CONFIG_PATH", "YF_BOOTSTRAP_PASSWORD"}
            }
            env.update({
                "App__ConnectionString": (
                    f"Server={_quoted_connection_value(parsed.hostname)};Port={parsed.port or 3306};"
                    f"Database={db_name};User ID={_quoted_connection_value(db_user)};"
                    f"Password={_quoted_connection_value(db_password)}"
                ),
                "App__JwtSecret": secrets.token_urlsafe(48),
                "App__StorageRoot": str(storage),
                "App__WebBaseUrl": base,
                "App__CookieSecure": "false",
                "App__TrustLoopbackProxy": "false",
                "App__WorkerEnabled": "false",
                "App__AccessTtlMinutes": "30",
                "App__RefreshTtlDays": "1",
                "App__UploadMaxFileSize": "1048576",
                "App__UploadChunkSize": "262144",
                "App__Smtp__Host": "",
                "App__Smtp__Port": "465",
                "App__Smtp__Username": "",
                "App__Smtp__Password": "",
                "App__Smtp__From": "",
                "App__Smtp__Security": "Auto",
                "ASPNETCORE_URLS": base,
                "URLS": base,
                "YF_BOOTSTRAP_PASSWORD": initial_password,
                "Logging__LogLevel__Default": "Warning",
            })
            initialized = subprocess.run(
                ["dotnet", str(dll), "--initialize-database"],
                cwd=api_dir,
                env=env,
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
                timeout=60,
            )
            if initialized.returncode != 0:
                raise CheckFailure("isolated database initialization failed")
            check("owned database initialized", len(initial_password) <= 17)
            env.pop("YF_BOOTSTRAP_PASSWORD", None)

            log = stack.enter_context(open(Path(temp) / "api.log", "wb"))
            process = subprocess.Popen(
                ["dotnet", str(dll)], cwd=api_dir, env=env, stdout=log, stderr=log,
            )
            stack.callback(_stop_process, process)
            admin = Client(base)
            for _ in range(150):
                if process.poll() is not None:
                    raise CheckFailure("isolated API exited during startup")
                try:
                    admin.call("GET", "/health")
                    break
                except (OSError, CheckFailure):
                    time.sleep(0.1)
            else:
                raise CheckFailure("isolated API health timeout")
            check("owned API is healthy", True)

            first_login = admin.login("admin", initial_password)
            if not first_login["mustChangePassword"]:
                raise CheckFailure("bootstrap login did not require a password change")
            admin.call("PUT", "/api/v1/auth/password", {
                "oldPassword": initial_password,
                "newPassword": changed_password,
            })
            admin.token = None
            second_login = admin.login("admin", changed_password)
            check("bootstrap account changed password and reauthenticated",
                  not second_login["mustChangePassword"])

            supplier = Client(base)
            outsider = Client(base)
            probe = SignalRProbe(node_script)
            stack.callback(probe.close)
            run_signalr_checks(admin, supplier, outsider, conn, probe, check, changed_password)
            probe.close()
            probe = None
            _stop_process(process)
            process = None
        print(f"PASS {len(passed)} SignalR checks; owned resources only", flush=True)
        return 0
    except CheckFailure as error:
        print("FAIL " + str(error), file=sys.stderr, flush=True)
        return 1
    except (OSError, pymysql.MySQLError, subprocess.SubprocessError) as error:
        print("FAIL infrastructure " + type(error).__name__, file=sys.stderr, flush=True)
        return 1
    except Exception as error:
        print("FAIL unexpected " + type(error).__name__, file=sys.stderr, flush=True)
        return 1
    finally:
        if probe is not None:
            probe.close()
        _stop_process(process)
        if created:
            try:
                with conn.cursor() as cursor:
                    cursor.execute(f"DROP DATABASE `{db_name}`")
            except pymysql.MySQLError:
                print("FAIL owned database cleanup", file=sys.stderr, flush=True)
        conn.close()


if __name__ == "__main__":
    raise SystemExit(main())
