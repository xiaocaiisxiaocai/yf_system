"""Developer-only: export schema/seeds from a newly migrated disposable database.

Never exports business data. Credentials are loaded in memory from ignored local config.
"""
import datetime
import json
import os
from pathlib import Path
import secrets
import subprocess
import tomllib
import urllib.parse
import uuid
import pymysql

root = Path(__file__).resolve().parents[2]
config = tomllib.loads((root / "yf_server/config.local.toml").read_text(encoding="utf-8-sig"))
url = urllib.parse.urlsplit(config["database"]["url"])
if url.hostname not in ("127.0.0.1", "localhost", "::1"):
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
    env["DATABASE_URL"] = urllib.parse.urlunsplit((url.scheme, url.netloc, "/" + name, url.query, ""))
    env["YF_BOOTSTRAP_PASSWORD"] = secrets.token_urlsafe(24)
    result = subprocess.run([str(root / ".runlogs/iis-release/server/migration.exe"), "up"], env=env, capture_output=True)
    if result.returncode:
        raise RuntimeError("Disposable database migration failed; output withheld to protect connection details")
    conn.select_db(name)
    baseline = {"sourceCommit": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=root, text=True).strip(),
                "source": "yf_server/migration/src through m20260911_000017_auth_session_families", "tables": [], "seeds": {}}
    with conn.cursor() as c:
        c.execute("SHOW TABLES")
        tables = [row[0] for row in c.fetchall() if row[0] != "project_workflow_cleanup_paths"]
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
    out = root / "server_dotnet/Yf.Api/Infrastructure/schema-baseline.json"
    out.write_text(json.dumps(baseline, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"Exported {len(tables)} tables; only built-in roles/permissions/config/migration seed rows; no users or credentials")
finally:
    if created:
        with conn.cursor() as c:
            c.execute(f"DROP DATABASE `{name}`")
        print("Disposable baseline database removed; business database unchanged")
    conn.close()
