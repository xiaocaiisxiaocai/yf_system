"""Run the real HTTP E2E suite on a disposable local database and server process.

Uses the existing Python/PyMySQL and compiled server dependencies. SMTP is disabled.
No running application is stopped; all resources created here are removed on exit.
"""
import json
import os
from pathlib import Path
import re
import secrets
import socket
import subprocess
import sys
import tempfile
import time
import tomllib
import urllib.error
import urllib.parse
import urllib.request
import uuid

import pymysql


ROOT = Path(__file__).resolve().parents[1]


def request(base, method, path, data=None, token=None):
    headers = {"Content-Type": "application/json"}
    if token:
        headers["Authorization"] = "Bearer " + token
    body = json.dumps(data).encode() if data is not None else None
    req = urllib.request.Request(base + path, data=body, headers=headers, method=method)
    with urllib.request.urlopen(req, timeout=5) as response:
        return json.load(response)


def main():
    source = Path(os.environ.get("YF_CONFIG", str(ROOT / "config.local.toml")))
    local = tomllib.loads(source.read_text(encoding="utf-8-sig"))
    url = urllib.parse.urlsplit(os.environ.get("YF_DATABASE_URL", local["database"]["url"]))
    if url.hostname not in ("localhost", "127.0.0.1", "::1"):
        raise SystemExit("HTTP 隔离测试仅允许本机 MySQL")
    executable = ROOT / "target" / "debug" / ("server.exe" if os.name == "nt" else "server")
    if not executable.is_file():
        raise SystemExit("请先在 yf_server 运行 cargo build -p server --locked --offline")

    database = "yf_test_http_" + uuid.uuid4().hex
    connection = pymysql.connect(
        host=url.hostname, port=url.port or 3306,
        user=urllib.parse.unquote(url.username or ""),
        password=urllib.parse.unquote(url.password or ""), autocommit=True,
    )
    created = False
    process = None
    try:
        with connection.cursor() as cursor:
            cursor.execute(f"CREATE DATABASE `{database}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci")
            created = True
        with tempfile.TemporaryDirectory(prefix="yf_http_test_") as directory:
            temp = Path(directory)
            with socket.socket() as listener:
                listener.bind(("127.0.0.1", 0))
                port = listener.getsockname()[1]
            base = f"http://127.0.0.1:{port}"
            cfg = tomllib.loads((ROOT / "config.toml").read_text(encoding="utf-8-sig"))
            cfg["server"] = {"addr": f"127.0.0.1:{port}", "trust_loopback_proxy": False}
            cfg["database"] = {
                "url": urllib.parse.urlunsplit((url.scheme, url.netloc, "/" + database, url.query, "")),
                "auto_migrate": True,
            }
            cfg["storage"]["root"] = str(temp / "storage")
            cfg["jwt"]["secret"] = secrets.token_urlsafe(48)
            cfg["jwt"]["cookie_secure"] = False
            cfg["smtp"] = {"host": "", "port": 465, "username": "", "password": "", "from": ""}
            cfg["web"]["base_url"] = base
            config_path = temp / "config.private.toml"
            config_path.write_text("\n".join(
                f"[{section}]\n" + "\n".join(f"{key} = {json.dumps(value, ensure_ascii=False)}" for key, value in values.items())
                for section, values in cfg.items()
            ), encoding="utf-8")
            env = {key: value for key, value in os.environ.items() if not key.startswith("YF_")}
            env.update(YF_CONFIG=str(config_path), RUST_LOG="info,sea_orm=warn,sqlx=warn", PYTHONIOENCODING="utf-8")
            log_path = temp / "server.private.log"
            with log_path.open("wb") as log:
                process = subprocess.Popen(
                    [str(executable)], cwd=ROOT, env=env, stdout=log, stderr=subprocess.STDOUT,
                    creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0,
                )
                try:
                    deadline = time.monotonic() + 45
                    while True:
                        if process.poll() is not None:
                            raise RuntimeError("隔离服务启动失败；未连接现有业务服务")
                        try:
                            if request(base, "GET", "/health") == {"db": "up", "status": "ok"}:
                                break
                        except (urllib.error.URLError, TimeoutError):
                            pass
                        if time.monotonic() >= deadline:
                            raise RuntimeError("隔离服务健康检查超时")
                        time.sleep(0.2)
                    # The bootstrap password exists only in this temporary private log.
                    text = re.sub(r"\x1b\[[0-9;]*m", "", log_path.read_text(encoding="utf-8"))
                    initial = re.search(r"\bpassword=([^\s]+)", text)
                    if initial is None:
                        raise RuntimeError("未发现隔离库首次部署凭据")
                    auth = request(base, "POST", "/api/v1/auth/login", {"employeeNo": "admin", "password": initial[1]})
                    if auth.get("mustChangePassword") is not True:
                        raise RuntimeError("隔离管理员未要求首次改密")
                    password = secrets.token_urlsafe(12)
                    request(base, "PUT", "/api/v1/auth/password", {"oldPassword": initial[1], "newPassword": password}, auth["accessToken"])
                    print("隔离服务已就绪；首次改密已验证；SMTP 禁用；凭据不输出", flush=True)
                    env.update(YF_E2E_ISOLATED="1", YF_E2E_BASE_URL=base + "/api/v1", YF_E2E_ADMIN_PASSWORD=password)
                    result = subprocess.run([sys.executable, str(ROOT / "e2e_test.py")], cwd=ROOT, env=env)
                    return result.returncode
                finally:
                    if process.poll() is None:
                        process.terminate()
                        try:
                            process.wait(timeout=10)
                        except subprocess.TimeoutExpired:
                            process.kill()
                            process.wait(timeout=10)
                    process = None
    finally:
        # The name is generated above and never comes from configuration or HTTP data.
        if created:
            with connection.cursor() as cursor:
                cursor.execute(f"DROP DATABASE `{database}`")
            print("HTTP 隔离数据库、存储及服务已清理；业务库与既有服务未修改", flush=True)
        connection.close()


if __name__ == "__main__":
    raise SystemExit(main())
