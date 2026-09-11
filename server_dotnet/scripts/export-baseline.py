"""Developer-only: export schema/seeds from a newly migrated disposable database.

Never exports business data. Credentials are supplied through process-scoped YF_TEST_DATABASE_URL. No Rust tools or configuration are read.
"""
import argparse
import datetime
import json
import os
from pathlib import Path
import secrets
import subprocess
import tempfile
import urllib.parse
import uuid
import pymysql

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--output", type=Path, required=True, help="New JSON file for review; existing files are never overwritten")
out = parser.parse_args().output.resolve()
if out.exists():
    raise SystemExit("Output already exists; refusing to overwrite a reviewed baseline")
if not out.parent.is_dir():
    raise SystemExit("Output parent directory must already exist")
root = Path(__file__).resolve().parents[2]
config_url = os.environ.get("YF_TEST_DATABASE_URL")
if not config_url:
    raise SystemExit("Set YF_TEST_DATABASE_URL to a local MySQL test-administration URL")
url = urllib.parse.urlsplit(config_url)
if url.scheme != "mysql" or url.hostname not in ("127.0.0.1", "localhost", "::1"):
    raise SystemExit("Only local MySQL is permitted")
name = "yf_test_baseline_" + uuid.uuid4().hex
conn = pymysql.connect(host=url.hostname, port=url.port or 3306,
                       user=urllib.parse.unquote(url.username or ""),
                       password=urllib.parse.unquote(url.password or ""), autocommit=True)
created = False
try:
    with conn.cursor() as c:
        c.execute(f"CREATE DATABASE `{name}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci")
        created = True
    env = os.environ.copy()
    env.pop("YF_CONFIG_PATH", None)
    env["YF_BOOTSTRAP_PASSWORD"] = secrets.token_urlsafe(24)
    def cs(value):
        return '\"' + str(value).replace('\"', '\"\"') + '\"'
    with tempfile.TemporaryDirectory(prefix="yf_dotnet_baseline_") as temp:
        env.update({"App__ConnectionString": f"Server={cs(url.hostname)};Port={url.port or 3306};Database={name};User ID={cs(urllib.parse.unquote(url.username or ''))};Password={cs(urllib.parse.unquote(url.password or ''))}",
                    "App__JwtSecret": secrets.token_urlsafe(48), "App__StorageRoot": temp,
                    "App__WebBaseUrl": "http://127.0.0.1:8080", "App__WorkerEnabled": "false"})
        api = root / "server_dotnet/Yf.Api"
        result = subprocess.run(["dotnet", str(api / "bin/Debug/net10.0/Yf.Api.dll"), "--initialize-database"], cwd=api, env=env, capture_output=True)
        if result.returncode:
            raise RuntimeError("Disposable .NET initialization failed; output withheld to protect connection details")
    conn.select_db(name)
    baseline = {"sourceCommit": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=root, text=True).strip(),
                "source": "server_dotnet/Yf.Api .NET initializer; schema import baseline 17; owned upgrades stay in SchemaMigrations", "tables": [], "seeds": {}}
    with conn.cursor() as c:
        c.execute("SHOW TABLES")
        tables = [row[0] for row in c.fetchall() if row[0] not in ("project_workflow_cleanup_paths", "yf_schema_migrations")]
        for table in tables:
            c.execute(f"SHOW CREATE TABLE `{table}`")
            ddl = c.fetchone()[1]
            import re
            ddl = re.sub(r" AUTO_INCREMENT=\d+", "", ddl)
            baseline["tables"].append({"name": table, "sql": ddl})
        for table in ("roles", "permissions", "role_permissions", "system_configs", "seaql_migrations"):
            c.execute(f"SELECT * FROM `{table}`")
            columns = [x[0] for x in c.description]
            rows = []
            for row in c.fetchall():
                rows.append({key: ("2026-09-11T00:00:00" if isinstance(value, datetime.datetime) else value)
                             for key, value in zip(columns, row)})
            baseline["seeds"][table] = rows
    with out.open("x", encoding="utf-8") as exported:
        exported.write(json.dumps(baseline, ensure_ascii=False, indent=2) + "\n")
    print(f"Exported {len(tables)} tables; only built-in roles/permissions/config/migration seed rows; no users or credentials")
finally:
    if created:
        with conn.cursor() as c:
            c.execute(f"DROP DATABASE `{name}`")
        print("Disposable baseline database removed; business database unchanged")
    conn.close()
