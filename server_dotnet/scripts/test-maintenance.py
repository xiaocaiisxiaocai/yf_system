"""Exercise real backup/restore clients against owned local schemas, never IIS."""
import hashlib
import html
import json
import os
from pathlib import Path
import secrets
import subprocess
import tempfile
from urllib.parse import unquote, urlsplit

import pymysql

source = Path(__file__).resolve().parents[2]
url = urlsplit(os.environ.get("YF_TEST_DATABASE_URL", ""))
if url.scheme != "mysql" or url.hostname not in ("localhost", "127.0.0.1", "::1"):
    raise SystemExit("Set YF_TEST_DATABASE_URL to an explicit local test-management connection")
connection = pymysql.connect(host=url.hostname, port=url.port or 3306,
                             user=unquote(url.username or ""), password=unquote(url.password or ""),
                             charset="utf8mb4", autocommit=True)
schemas = ["yf_test_maintenance_" + secrets.token_hex(8) for _ in range(3)]
created = []
try:
    with connection.cursor() as cursor:
        for schema in schemas:
            cursor.execute(f"CREATE DATABASE `{schema}` CHARACTER SET utf8mb4")
            created.append(schema)
        cursor.execute(f"CREATE TABLE `{schemas[0]}`.evidence(id INT PRIMARY KEY,content TEXT,payload LONGBLOB)")
        payload = bytes(range(256)) * 100
        cursor.execute(f"INSERT INTO `{schemas[0]}`.evidence VALUES(1,%s,%s)", ("中文备份；quotes ' \"", payload))
        cursor.execute(f"CREATE PROCEDURE `{schemas[2]}`.keep_procedure() SELECT 'preserve-procedure'")
        cursor.execute(f"""CREATE EVENT `{schemas[2]}`.keep_event
                        ON SCHEDULE EVERY 1 DAY STARTS CURRENT_TIMESTAMP + INTERVAL 1 DAY
                        DO SET @yf_maintenance_guard = 1""")
        cursor.execute("""SELECT ROUTINE_DEFINITION FROM information_schema.routines
                        WHERE routine_schema=%s AND routine_name='keep_procedure'""", (schemas[2],))
        procedure_before = cursor.fetchone()[0]
        cursor.execute("""SELECT event_definition,status FROM information_schema.events
                        WHERE event_schema=%s AND event_name='keep_event'""", (schemas[2],))
        event_before = cursor.fetchone()
    with tempfile.TemporaryDirectory(prefix="yf_maintenance_") as directory:
        root = Path(directory)
        app = root / "application"
        (app / "wwwroot").mkdir(parents=True)
        (app / "Yf.Api.dll").write_bytes(b"isolated application payload")
        config_path = root / "source.json"
        web_config = f"""<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <location path="." inheritInChildApplications="false">
    <system.webServer>
      <handlers><add name="aspNetCore" path="*" verb="*" modules="AspNetCoreModuleV2" resourceType="Unspecified" /></handlers>
      <aspNetCore processPath="dotnet" arguments=".\\Yf.Api.dll" hostingModel="inprocess" stdoutLogEnabled="false">
        <environmentVariables>
          <environmentVariable name="YF_CONFIG_PATH" value="{html.escape(str(config_path), quote=True)}" />
          <environmentVariable name="ASPNETCORE_ENVIRONMENT" value="Production" />
        </environmentVariables>
      </aspNetCore>
    </system.webServer>
  </location>
</configuration>
"""
        (app / "web.config").write_text(web_config, encoding="utf-8")
        (app / "wwwroot/index.html").write_text("<p>本机隔离恢复</p>", encoding="utf-8")
        storage = root / "source-storage"
        (storage / "files").mkdir(parents=True)
        (storage / "files/中文 file.bin").write_bytes(payload)
        (storage / "empty-directory").mkdir()
        def quote(value):
            return '"' + str(value).replace('"', '""') + '"'
        for name, schema, path in (("source", schemas[0], storage),
                                   ("target", schemas[1], root / "target-storage"),
                                   ("second", schemas[1], root / "second-storage"),
                                   ("guarded", schemas[2], root / "guarded-storage")):
            cs = (f"Server={quote(url.hostname)};Port={url.port or 3306};Database={schema};"
                  f"User ID={quote(unquote(url.username or ''))};Password={quote(unquote(url.password or ''))};SslMode=Preferred")
            (root / (name + ".json")).write_text(json.dumps({"App": {
                "ConnectionString": cs, "StorageRoot": str(path),
                "WebBaseUrl": "https://isolated.invalid", "CookieSecure": True
            }}), encoding="utf-8")
        command = ["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                   str(source / "server_dotnet/scripts/test-maintenance.ps1"), "-FixtureRoot", str(root)]
        child_env = os.environ.copy()
        # A Python child of PowerShell 7 may inherit incompatible PS7 modules.
        # Keep this change in the Windows PowerShell 5.1 test process only.
        child_env["PSModulePath"] = os.pathsep.join([
            str(Path(os.environ["WINDIR"]) / "System32/WindowsPowerShell/v1.0/Modules"),
            str(Path(os.environ["ProgramFiles"]) / "WindowsPowerShell/Modules"),
        ])
        result = subprocess.run(command, env=child_env, capture_output=True, text=True, encoding="utf-8", errors="replace")
        print(result.stdout, end="")
        if result.returncode:
            # The script suppresses database diagnostics that could contain credentials.
            print(result.stderr)
            raise SystemExit(result.returncode)
        with connection.cursor() as cursor:
            for schema in schemas[:2]:
                cursor.execute(f"SELECT content,payload FROM `{schema}`.evidence WHERE id=1")
                text, recovered = cursor.fetchone()
                assert text == "中文备份；quotes ' \"" and recovered == payload
            cursor.execute("""SELECT ROUTINE_DEFINITION FROM information_schema.routines
                            WHERE routine_schema=%s AND routine_name='keep_procedure'""", (schemas[2],))
            assert cursor.fetchone()[0] == procedure_before
            cursor.execute("""SELECT event_definition,status FROM information_schema.events
                            WHERE event_schema=%s AND event_name='keep_event'""", (schemas[2],))
            assert cursor.fetchone() == event_before
        assert (root / "target-storage/empty-directory").is_dir()
        report = {"realMySqlBackupRestore": True, "originalSchemaUnchanged": True,
                  "restoredUnicodeAndBinaryExact": True, "emptyDirectoryPreserved": True,
                  "tablelessRoutineAndEventPreserved": True,
                  "payloadSha256": hashlib.sha256(payload).hexdigest(),
                  "targetIisTested": False}
        report_path = source / ".runlogs/maintenance-results.json"
        report_path.parent.mkdir(exist_ok=True)
        report_path.write_text(json.dumps(report, indent=2), encoding="utf-8")
        print("PASS real database rows, UTF-8 and binary bytes; IIS was not used")
finally:
    with connection.cursor() as cursor:
        for schema in created:
            if not schema.startswith("yf_test_maintenance_"):
                raise RuntimeError("Unexpected cleanup schema")
            cursor.execute(f"DROP DATABASE `{schema}`")
    connection.close()
    print("Owned maintenance test schemas removed")
