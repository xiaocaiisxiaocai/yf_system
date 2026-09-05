"""Create a disposable local database; never run tests against the configured business database."""
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import tomllib
import urllib.parse
import uuid
import pymysql

root = Path(__file__).resolve().parents[1]
config_path = Path(os.environ.get("YF_CONFIG", str(root / "config.local.toml")))
config = tomllib.loads(config_path.read_text(encoding="utf-8-sig"))
url = urllib.parse.urlsplit(os.environ.get("YF_DATABASE_URL", config["database"]["url"]))
if url.hostname not in ("127.0.0.1", "localhost", "::1"):
    raise SystemExit("隔离测试仅允许本机 MySQL")
name = "yf_test_" + uuid.uuid4().hex
connection = pymysql.connect(host=url.hostname, port=url.port or 3306,
    user=urllib.parse.unquote(url.username or ""), password=urllib.parse.unquote(url.password or ""), autocommit=True)
created = False
try:
    with connection.cursor() as cursor:
        cursor.execute(f"CREATE DATABASE `{name}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci")
        created = True
    with tempfile.TemporaryDirectory(prefix="yf_test_storage_") as storage:
        env = os.environ.copy()
        env["YF_TEST_DATABASE_URL"] = urllib.parse.urlunsplit((url.scheme, url.netloc, "/" + name, url.query, ""))
        env["YF_TEST_STORAGE_ROOT"] = storage
        result = subprocess.run(["cargo", "test", "-p", "server", "--locked", "--offline", *sys.argv[1:], "--", "--ignored", "--test-threads=1"], cwd=root, env=env)
    sys.exit(result.returncode)
finally:
    if created:
        with connection.cursor() as cursor:
            cursor.execute(f"DROP DATABASE `{name}`")
        print("隔离测试库已清理；业务库未修改", flush=True)
    connection.close()
