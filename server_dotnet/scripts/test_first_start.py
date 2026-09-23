"""Exercise first-start initialization from a published API using disposable local resources."""
import json
import contextlib
import os
from pathlib import Path
import secrets
import socket
import subprocess
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid

import pymysql


def verify_first_start(package: Path, temp_root: Path):
    url = urllib.parse.urlsplit(os.environ.get("YF_TEST_DATABASE_URL", ""))
    if url.scheme != "mysql" or url.hostname not in ("localhost", "127.0.0.1", "::1"):
        raise RuntimeError("First-start testing requires an explicit local MySQL administration URL")
    user, password = urllib.parse.unquote(url.username or ""), urllib.parse.unquote(url.password or "")
    database = "yf_start_" + uuid.uuid4().hex[:24]
    admin = pymysql.connect(host=url.hostname, port=url.port or 3306, user=user, password=password, autocommit=True)
    owned = None
    process = None

    def stop():
        nonlocal process
        if process is not None:
            if process.poll() is None:
                process.terminate()
                try:
                    process.wait(timeout=15)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait()
            process = None

    try:
        with admin.cursor() as cursor:
            cursor.execute("SELECT COUNT(*) FROM information_schema.schemata WHERE schema_name=%s", (database,))
            if cursor.fetchone()[0]:
                raise RuntimeError("Disposable first-start database unexpectedly exists")
        owned = database
        with tempfile.TemporaryDirectory(prefix="yf_first_start_", dir=temp_root) as temp, contextlib.ExitStack() as stack:
            stack.callback(stop)
            root = Path(temp)
            content = root / "app"
            content.mkdir()
            with socket.socket() as listener:
                listener.bind(("127.0.0.1", 0))
                port = listener.getsockname()[1]
            base = f"http://127.0.0.1:{port}"
            initial = "Yf!" + secrets.token_hex(6)
            changed = "Yf!" + secrets.token_hex(6)
            settings = json.loads((package / "appsettings.Production.json").read_text(encoding="utf-8-sig"))

            def cs(value):
                return '"' + str(value).replace('"', '""') + '"'

            settings["App"].update({
                "ConnectionString": f"Server={cs(url.hostname)};Port={url.port or 3306};Database={database};User ID={cs(user)};Password={cs(password)}",
                "JwtSecret": secrets.token_urlsafe(48), "StorageRoot": str(root / "storage"),
                "WebBaseUrl": base, "CookieSecure": False, "WorkerEnabled": False,
                "BootstrapPassword": initial, "Smtp": {"Host": ""},
            })
            if settings["App"].get("AutoInitializeDatabase") is not True:
                raise RuntimeError("Published configuration does not enable first-start initialization")
            # Only the disposable content root is changed. Original package hashes remain intact.
            (content / "appsettings.json").write_bytes((package / "appsettings.json").read_bytes())
            production = content / "appsettings.Production.json"
            production.write_text(json.dumps(settings), encoding="utf-8")
            env = {k: v for k, v in os.environ.items() if not k.lower().startswith(("app__", "app:"))
                   and k.upper() not in {"YF_CONFIG_PATH", "YF_BOOTSTRAP_PASSWORD"}}
            env.update({"DOTNET_ENVIRONMENT": "Production", "ASPNETCORE_ENVIRONMENT": "Production"})

            def call(path, body=None, token=None, method=None):
                headers = {"Origin": base, "Content-Type": "application/json"}
                if token:
                    headers["Authorization"] = "Bearer " + token
                request = urllib.request.Request(base + path, headers=headers, method=method,
                    data=None if body is None else json.dumps(body).encode())
                with urllib.request.urlopen(request, timeout=5) as response:
                    data = response.read()
                    return json.loads(data) if data else None

            with open(root / "startup.log", "wb") as log:
                def start():
                    nonlocal process
                    process = subprocess.Popen(["dotnet", str(package / "Yf.Api.dll"),
                        "--contentRoot", str(content), "--urls", base], cwd=content, env=env, stdout=log, stderr=log)
                    deadline = time.monotonic() + 90
                    while time.monotonic() < deadline:
                        if process.poll() is not None:
                            raise RuntimeError("Published first-start process exited before health was ready")
                        try:
                            if call("/health").get("db") == "up":
                                return
                        except (OSError, urllib.error.URLError):
                            pass
                        time.sleep(0.2)
                    raise RuntimeError("Published first-start health timed out")

                start()  # No CREATE DATABASE or --initialize-database before this launch.
                login = call("/api/v1/auth/login", {"employeeNo": "admin", "password": initial})
                if not login["mustChangePassword"] or not login["user"]["isSystemAdmin"]:
                    raise RuntimeError("First-start admin contract failed")
                call("/api/v1/auth/password", {"oldPassword": initial, "newPassword": changed}, login["accessToken"], "PUT")
                stop()
                settings["App"].pop("BootstrapPassword")
                production.write_text(json.dumps(settings), encoding="utf-8")
                start()
                login = call("/api/v1/auth/login", {"employeeNo": "admin", "password": changed})
                if login["mustChangePassword"]:
                    raise RuntimeError("Restart reset administrator bootstrap state")
                for path in ("/appsettings.json", "/appsettings.Production.json"):
                    try:
                        call(path)
                        raise RuntimeError("Published configuration is exposed over HTTP")
                    except urllib.error.HTTPError as error:
                        if error.code != 404:
                            raise
                stop()
                with admin.cursor() as cursor:
                    cursor.execute(f"SELECT COUNT(*) FROM `{database}`.users")
                    if cursor.fetchone()[0] != 1:
                        raise RuntimeError("Restart duplicated users")
            print("PASS published Production first-start creates database and admin; login, password change and restart preserve account", flush=True)
            return {"databaseCreatedByStartup": True, "productionJsonLoaded": True, "adminLogin": True,
                    "passwordChange": True, "restartWithoutBootstrapPassword": True, "configurationHttpHidden": True}
    finally:
        stop()
        try:
            if owned:
                with admin.cursor() as cursor:
                    cursor.execute(f"DROP DATABASE IF EXISTS `{owned}`")
        finally:
            admin.close()
