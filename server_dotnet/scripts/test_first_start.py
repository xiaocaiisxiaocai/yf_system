"""Exercise first-start initialization from a published API using disposable local resources."""
import json
import contextlib
import gzip
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


def _http_bytes(base, path, *, headers=None, method="GET", expected=200):
    request = urllib.request.Request(base + path, headers={"Origin": base, **(headers or {})}, method=method)
    try:
        response = urllib.request.urlopen(request, timeout=10)
    except urllib.error.HTTPError as error:
        response = error
    with response:
        body = response.read()
        status = response.getcode()
        if status != expected:
            raise RuntimeError(f"Published static HTTP {method} {path} returned {status}, expected {expected}")
        return body, response.headers


def _packaged_public_path(package, relative):
    public_root = (package / "wwwroot").resolve()
    path = (public_root / relative).resolve()
    if not path.is_relative_to(public_root) or not path.is_file():
        raise RuntimeError("Precompression manifest refers outside the packaged public root")
    return path


def verify_precompressed_static_http(base, package):
    manifest_path = package / "precompressed-assets.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8-sig"))
    if manifest.get("schemaVersion") != 1 or manifest.get("root") != "wwwroot":
        raise RuntimeError("Published precompression manifest is unsupported")
    selected = {}
    for suffix in (".js", ".css"):
        selected[suffix] = next((asset for asset in manifest.get("assets", [])
            if asset.get("path", "").lower().endswith(suffix)
            and all(asset.get("encodings", {}).get(name, {}).get("status") == "generated"
                    for name in ("br", "gzip"))), None)
        if selected[suffix] is None:
            raise RuntimeError(f"Published manifest lacks a Brotli+gzip {suffix} asset for HTTP verification")

    asset = selected[".js"]
    request_path = "/" + urllib.parse.quote(asset["path"], safe="/")
    source = _packaged_public_path(package, asset["path"]).read_bytes()
    encoded = {
        name: _packaged_public_path(package, asset["encodings"][name]["path"]).read_bytes()
        for name in ("br", "gzip")
    }

    identity, identity_headers = _http_bytes(base, request_path,
        headers={"Accept-Encoding": "br;q=0.2, gzip;q=0.4, identity;q=1"})
    if identity != source or identity_headers.get("Content-Encoding"):
        raise RuntimeError("Published identity static representation differs from the packaged source")
    if int(identity_headers.get("Content-Length", -1)) != len(source):
        raise RuntimeError("Published identity static Content-Length is incorrect")

    br, br_headers = _http_bytes(base, request_path,
        headers={"Accept-Encoding": "gzip;q=0.4, br;q=1, identity;q=0"})
    if br != encoded["br"] or br_headers.get("Content-Encoding") != "br":
        raise RuntimeError("Published Brotli response is not the packaged byte-exact representation")
    if int(br_headers.get("Content-Length", -1)) != len(encoded["br"]):
        raise RuntimeError("Published Brotli Content-Length is incorrect")
    if "accept-encoding" not in br_headers.get("Vary", "").lower():
        raise RuntimeError("Published precompressed response does not vary by Accept-Encoding")
    if "javascript" not in br_headers.get("Content-Type", "").lower():
        raise RuntimeError("Published JavaScript response lost its original content type")

    gzip_body, gzip_headers = _http_bytes(base, request_path,
        headers={"Accept-Encoding": "br;q=0.2, gzip;q=1, identity;q=0"})
    if gzip_body != encoded["gzip"] or gzip.decompress(gzip_body) != source \
            or gzip_headers.get("Content-Encoding") != "gzip":
        raise RuntimeError("Published gzip response is not the packaged byte-exact representation")

    head, head_headers = _http_bytes(base, request_path, method="HEAD",
        headers={"Accept-Encoding": "br, identity;q=0"})
    if head or head_headers.get("Content-Encoding") != "br" \
            or int(head_headers.get("Content-Length", -1)) != len(encoded["br"]):
        raise RuntimeError("Published compressed HEAD metadata is incorrect")
    etag = br_headers.get("ETag")
    if not etag:
        raise RuntimeError("Published precompressed response lacks an ETag")
    not_modified, not_modified_headers = _http_bytes(base, request_path, expected=304,
        headers={"Accept-Encoding": "br, identity;q=0", "If-None-Match": etag})
    if not_modified or not_modified_headers.get("ETag") != etag:
        raise RuntimeError("Published precompressed conditional request did not preserve its ETag")

    end = min(31, len(source) - 1)
    partial, range_headers = _http_bytes(base, request_path, expected=206,
        headers={"Accept-Encoding": "br, gzip", "Range": f"bytes=0-{end}"})
    if partial != source[:end + 1] or range_headers.get("Content-Encoding") \
            or range_headers.get("Content-Range") != f"bytes 0-{end}/{len(source)}":
        raise RuntimeError("Published Range request did not fall back to a valid identity 206 response")

    css_asset = selected[".css"]
    css_path = "/" + urllib.parse.quote(css_asset["path"], safe="/")
    css, css_headers = _http_bytes(base, css_path,
        headers={"Accept-Encoding": "br, identity;q=0"})
    packaged_css = _packaged_public_path(package, css_asset["encodings"]["br"]["path"]).read_bytes()
    if css != packaged_css or css_headers.get("Content-Encoding") != "br" \
            or not css_headers.get("Content-Type", "").lower().startswith("text/css"):
        raise RuntimeError("Published CSS precompressed response or content type is incorrect")

    index = next((asset for asset in manifest.get("assets", []) if asset.get("path") == "index.html"
        and asset.get("encodings", {}).get("br", {}).get("status") == "generated"), None)
    if index is None:
        raise RuntimeError("Published manifest lacks a Brotli index.html representation")
    root_body, root_headers = _http_bytes(base, "/", headers={"Accept-Encoding": "br, identity;q=0"})
    packaged_index = _packaged_public_path(package, index["encodings"]["br"]["path"]).read_bytes()
    if root_body != packaged_index or root_headers.get("Content-Encoding") != "br" \
            or not root_headers.get("Content-Type", "").lower().startswith("text/html"):
        raise RuntimeError("Published root default-file rewrite did not serve precompressed index.html")

    health_body, health_headers = _http_bytes(base, "/health",
        headers={"Accept-Encoding": "br, gzip, identity;q=0"})
    if health_headers.get("Content-Encoding") or json.loads(health_body).get("db") != "up":
        raise RuntimeError("Published health API was compressed or unhealthy")
    return {"assets": [asset["path"], css_asset["path"]], "brotliPackagedBytes": True,
            "gzipDecompressionByteExact": True, "identityByteExact": True, "qualityNegotiation": True,
            "head": True, "conditional304": True,
            "identityRange206": True, "healthUncompressed": True, "rootIndexRewrite": True}


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
                        "--contentRoot", str(content), "--webroot", str(package / "wwwroot"), "--urls", base],
                        cwd=content, env=env, stdout=log, stderr=log)
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
                precompressed_static = verify_precompressed_static_http(base, package)
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
            print("PASS published Production first-start, account restart, and precompressed static HTTP contracts", flush=True)
            return {"databaseCreatedByStartup": True, "productionJsonLoaded": True, "adminLogin": True,
                    "passwordChange": True, "restartWithoutBootstrapPassword": True, "configurationHttpHidden": True,
                    "precompressedStaticHttp": precompressed_static}
    finally:
        stop()
        try:
            if owned:
                with admin.cursor() as cursor:
                    cursor.execute(f"DROP DATABASE IF EXISTS `{owned}`")
        finally:
            admin.close()
