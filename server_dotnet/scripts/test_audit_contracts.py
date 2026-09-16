"""Exercise audit-log enrichment against an owned disposable API/database.

Required environment:
  YF_TEST_DATABASE_URL  local MySQL administration URL (mysql://...)
  YF_TEST_API_DIR       directory containing a previously built Yf.Api.dll

The script never reads project configuration and never sends email. Credentials and
random fixture values stay in process memory; console output contains check names only.
"""

import contextlib
import http.cookiejar
import json
import os
from pathlib import Path
import secrets
import socket
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid

import pymysql

from test_business_acceptance import _create_project_group


class CheckFailure(AssertionError):
    pass


class Client:
    def __init__(self, base):
        self.base = base
        self.cookies = http.cookiejar.CookieJar()
        self.opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(self.cookies))
        self.token = None

    def call(self, method, path, body=None, expected=200):
        headers = {"Origin": self.base}
        if self.token:
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


def _query(**values):
    return urllib.parse.urlencode({key: value for key, value in values.items() if value is not None})


def _latest(client, action, target_id=None):
    result = client.call("GET", "/api/v1/admin/audit-logs?" + _query(
        action=action,
        targetId=target_id,
        page=1,
        pageSize=100,
    ))
    if not result["list"]:
        raise CheckFailure(f"missing audit action {action}")
    return result["list"][0]


def _permission_shape(items):
    return all(
        set(item) >= {"id", "code", "name"}
        and isinstance(item["id"], int)
        and bool(item["code"])
        and bool(item["name"])
        for item in items
    )


def _assign_admin_section(client, conn, suffix, admin_id):
    division = client.call("POST", "/api/v1/admin/departments", {
        "name": "审计事业部-" + suffix,
        "parentId": None,
        "sortNo": 140,
    })
    department = client.call("POST", "/api/v1/admin/departments", {
        "name": "审计部门-" + suffix,
        "parentId": division["id"],
        "sortNo": 141,
    })
    section = client.call("POST", "/api/v1/admin/departments", {
        "name": "审计课别-" + suffix,
        "parentId": department["id"],
        "sortNo": 142,
    })
    with conn.cursor() as cursor:
        cursor.execute("UPDATE users SET department_id=%s WHERE id=%s", (section["id"], admin_id))


def run_audit_checks(client, conn, check):
    """Run focused audit assertions using caller-owned isolated resources."""
    profile = client.call("GET", "/api/v1/auth/profile")
    actor_name = profile["user"]["realName"]
    admin_id = profile["user"]["id"]
    _assign_admin_section(client, conn, secrets.token_hex(4), admin_id)

    role_old_name = "审计历史角色-" + secrets.token_hex(4)
    role_new_name = "审计当前角色-" + secrets.token_hex(4)
    role_description = "owned audit regression fixture"
    role = client.call("POST", "/api/v1/admin/roles", {
        "name": role_old_name,
        "description": role_description,
    })
    role_id = role["id"]
    created = _latest(client, "ROLE_CREATE", role_id)
    context = created["detail"]["auditContext"]
    check("HTTP management audit records IP request ID and snapshots", bool(created["ip"])
          and created["actorName"] == actor_name
          and created["targetName"] == role_old_name
          and created["actorNameSource"] == "snapshot"
          and created["targetNameSource"] == "snapshot"
          and context["actorName"] == actor_name
          and context["targetName"] == role_old_name
          and bool(context["requestId"])
          and context["source"] == "HTTP")

    permissions = [item for item in client.call("GET", "/api/v1/permissions") if item["grantable"]]
    if len(permissions) < 2:
        raise CheckFailure("isolated administrator exposes fewer than two grantable permissions")
    first, second = permissions[:2]
    client.call("PUT", f"/api/v1/admin/roles/{role_id}/permissions", {
        "permissionIds": [first["id"], second["id"]],
    })
    assigned = _latest(client, "ROLE_ASSIGN_PERMS", role_id)["detail"]
    check("role audit lists added permission identities", _permission_shape(assigned["addedPermissions"])
          and {item["id"] for item in assigned["addedPermissions"]} == {first["id"], second["id"]}
          and assigned["removedPermissions"] == [])

    client.call("PUT", f"/api/v1/admin/roles/{role_id}/permissions", {
        "permissionIds": [second["id"]],
    })
    reassigned = _latest(client, "ROLE_ASSIGN_PERMS", role_id)["detail"]
    removed = reassigned["removedPermissions"]
    check("role audit lists removed permission ID code and name", _permission_shape(removed)
          and len(removed) == 1
          and removed[0] == {"id": first["id"], "code": first["code"], "name": first["name"]}
          and reassigned["addedPermissions"] == []
          and reassigned["changes"][0]["field"] == "permissions")

    client.call("PUT", f"/api/v1/admin/roles/{role_id}", {
        "name": role_new_name,
        "description": role_description,
    })
    renamed = _latest(client, "ROLE_UPDATE", role_id)
    check("role rename reports only the changed name", renamed["targetName"] == role_new_name
          and renamed["detail"]["changes"] == [{
              "field": "name", "label": "角色名称", "before": role_old_name, "after": role_new_name,
          }])

    actor_search = client.call("GET", "/api/v1/admin/audit-logs?" + _query(
        keyword=actor_name, page=1, pageSize=100,
    ))
    current_search = client.call("GET", "/api/v1/admin/audit-logs?" + _query(
        keyword=role_new_name, page=1, pageSize=100,
    ))
    history_search = client.call("GET", "/api/v1/admin/audit-logs?" + _query(
        keyword=role_old_name, page=1, pageSize=100,
    ))
    historical_role = next(
        (item for item in history_search["list"] if item["action"] == "ROLE_CREATE" and item["targetId"] == str(role_id)),
        None,
    )
    check("audit search covers employee current object and historical snapshot names",
          any(item["actorName"] == actor_name for item in actor_search["list"])
          and any(item["targetId"] == str(role_id) for item in current_search["list"])
          and historical_role is not None
          and historical_role["targetName"] == role_old_name
          and historical_role["targetNameSource"] == "snapshot")

    supplier_name = "审计供应商-" + secrets.token_hex(4)
    supplier = client.call("POST", "/api/v1/admin/suppliers", {
        "name": supplier_name,
        "remark": "owned audit regression fixture",
    })
    project_old_name = "审计旧项目-" + secrets.token_hex(4)
    project_new_name = "审计新项目-" + secrets.token_hex(4)
    group, project = _create_project_group(
        client, conn, supplier["id"], admin_id, project_old_name)
    project_id = project["id"]
    group_id = group["id"]
    group_detail = client.call("GET", f"/api/v1/project-groups/{group_id}")
    current_group = group_detail["group"]
    client.call("PUT", f"/api/v1/project-groups/{group_id}", {
        "name": project_new_name,
        "description": "修改后说明",
        "supplierId": supplier["id"],
        "workOrderNos": current_group["workOrderNos"],
        "machineModel": current_group["machineModel"],
        "robotVendorId": current_group["robotVendorId"],
        "robotModelId": current_group["robotModelId"],
        "responsibleUserId": current_group["responsibleUserId"],
        "sectionId": current_group["sectionId"],
        "priorityId": current_group["priorityId"],
        "expectedCompletionDate": current_group["expectedCompletionDate"],
    })
    project_update = _latest(client, "PROJECT_GROUP_UPDATE", group_id)
    check("main project update audit reports exact labeled before and after values",
          project_update["targetName"] == project_new_name
          and project_update["detail"]["changes"] == [
              {"field": "name", "label": "主项目名称", "before": project_old_name, "after": project_new_name},
              {"field": "description", "label": "项目说明",
               "before": "owned isolated full business acceptance fixture", "after": "修改后说明"},
          ])

    smtp_secret = secrets.token_urlsafe(24)
    smtp_response = client.call("PUT", "/api/v1/admin/system/mail-settings", {
        "host": "smtp.example.invalid",
        "port": 465,
        "username": "audit@example.invalid",
        "from": "audit@example.invalid",
        "security": "SslOnConnect",
        "password": smtp_secret,
    })
    smtp_audit = _latest(client, "CONFIG_UPDATE")
    with conn.cursor() as cursor:
        cursor.execute("SELECT cfg_value FROM system_configs WHERE cfg_key='mail.smtp'")
        stored_smtp = cursor.fetchone()[0]
    check("SMTP password stays out of API audit and stored plaintext",
          "password" not in smtp_response
          and smtp_response["hasPassword"]
          and smtp_secret not in stored_smtp
          and smtp_secret not in json.dumps(smtp_audit, ensure_ascii=False)
          and smtp_audit["detail"]["passwordChanged"] is True
          and all(change["field"] != "password" for change in smtp_audit["detail"]["changes"]))

    configs = {item["key"]: item["value"] for item in client.call("GET", "/api/v1/admin/system/configs")}
    previous_notify = configs["notify.enabled"]
    next_notify = "false" if previous_notify == "true" else "true"
    client.call("PUT", "/api/v1/admin/system/configs", {
        "items": [{"key": "notify.enabled", "value": next_notify}],
    })
    safe_config_audit = _latest(client, "CONFIG_UPDATE")
    check("safe system config audit exposes accurate before and after values",
          safe_config_audit["detail"]["changes"] == [{
              "field": "notify.enabled",
              "label": "邮件通知",
              "before": previous_notify,
              "after": next_notify,
          }])


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


def main():
    if not __debug__:
        raise SystemExit("Do not run with Python assertions disabled")
    admin_url = os.environ.get("YF_TEST_DATABASE_URL")
    api_value = os.environ.get("YF_TEST_API_DIR")
    if not admin_url or not api_value:
        raise SystemExit("Set YF_TEST_DATABASE_URL and YF_TEST_API_DIR for isolated audit testing")
    parsed = urllib.parse.urlsplit(admin_url)
    if parsed.scheme != "mysql" or parsed.hostname not in ("127.0.0.1", "localhost", "::1"):
        raise SystemExit("Isolated audit testing only allows local MySQL")
    api_dir = Path(api_value).resolve()
    dll = api_dir / "Yf.Api.dll"
    if not dll.is_file():
        raise SystemExit("YF_TEST_API_DIR does not contain Yf.Api.dll")

    db_name = "yf_test_audit_" + uuid.uuid4().hex
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
    passed = []
    outcome = 1

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
        with tempfile.TemporaryDirectory(prefix="yf_audit_test_") as temp, contextlib.ExitStack() as stack:
            storage = Path(temp) / "storage"
            storage.mkdir()
            with socket.socket() as listener:
                listener.bind(("127.0.0.1", 0))
                port = listener.getsockname()[1]
            base = f"http://127.0.0.1:{port}"
            initial_password = "Yf9!" + secrets.token_urlsafe(9)
            changed_password = "Yf9!" + secrets.token_urlsafe(9)
            env = {
                key: value for key, value in os.environ.items()
                if not key.lower().startswith(("app__", "app:"))
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
                "App__WorkerEnabled": "false",
                "App__Smtp__Host": "",
                "App__Smtp__Port": "0",
                "App__Smtp__Username": "",
                "App__Smtp__Password": "",
                "App__Smtp__From": "",
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
            check("owned database initialized", True)
            env.pop("YF_BOOTSTRAP_PASSWORD", None)

            log_path = Path(temp) / "api.log"
            log = stack.enter_context(open(log_path, "wb"))
            process = subprocess.Popen(
                ["dotnet", str(dll)],
                cwd=api_dir,
                env=env,
                stdout=log,
                stderr=log,
            )
            stack.callback(_stop_process, process)
            client = Client(base)
            for _ in range(150):
                if process.poll() is not None:
                    raise CheckFailure("isolated API exited during startup")
                try:
                    client.call("GET", "/health")
                    break
                except (OSError, CheckFailure):
                    time.sleep(0.1)
            else:
                raise CheckFailure("isolated API health timeout")
            check("owned API is healthy", True)

            first_login = client.login("admin", initial_password)
            if not first_login["mustChangePassword"]:
                raise CheckFailure("bootstrap login did not require a password change")
            client.call("PUT", "/api/v1/auth/password", {
                "oldPassword": initial_password,
                "newPassword": changed_password,
            })
            client.token = None
            second_login = client.login("admin", changed_password)
            check("bootstrap account changed password and reauthenticated", not second_login["mustChangePassword"])
            run_audit_checks(client, conn, check)
            _stop_process(process)
            process = None
        outcome = 0
    except CheckFailure as error:
        print("FAIL " + str(error), file=sys.stderr, flush=True)
    except (OSError, pymysql.MySQLError, subprocess.SubprocessError) as error:
        print("FAIL infrastructure " + type(error).__name__, file=sys.stderr, flush=True)
    except Exception as error:
        print("FAIL unexpected " + type(error).__name__, file=sys.stderr, flush=True)
    finally:
        _stop_process(process)
        if created:
            try:
                with conn.cursor() as cursor:
                    cursor.execute(f"DROP DATABASE `{db_name}`")
            except pymysql.MySQLError:
                print("FAIL owned database cleanup", file=sys.stderr, flush=True)
                outcome = 1
        conn.close()
    if outcome == 0:
        print(f"PASS {len(passed)} audit checks; owned resources only", flush=True)
    return outcome


if __name__ == "__main__":
    raise SystemExit(main())
