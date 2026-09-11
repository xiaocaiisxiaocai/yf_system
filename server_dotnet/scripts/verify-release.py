"""Validate and smoke-test the exact distributed ZIP in owned temporary directories."""
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import subprocess
import sys
import tempfile
import zipfile

source = Path(__file__).resolve().parents[2]
archive = Path(sys.argv[1]).resolve()
report_path = archive.with_suffix(".verification.json")
if report_path.exists():
    raise SystemExit("Verification report already exists; refusing to overwrite it")


def digest(path):
    with open(path, "rb") as file:
        return hashlib.file_digest(file, "sha256").hexdigest()


with tempfile.TemporaryDirectory(prefix="yf_dotnet_release_") as temp:
    extraction = Path(temp).resolve()
    with zipfile.ZipFile(archive) as zipped:
        for entry in zipped.infolist():
            path = PurePosixPath(entry.filename)
            if path.is_absolute() or ".." in path.parts or "\\" in entry.filename or ":" in entry.filename or (entry.external_attr >> 16) & 0o170000 == 0o120000:
                raise RuntimeError("Unsafe archive path")
        if zipped.testzip() is not None:
            raise RuntimeError("ZIP CRC failed")
        zipped.extractall(extraction)
    roots = list(extraction.iterdir())
    if len(roots) != 1 or not roots[0].is_dir():
        raise RuntimeError("Expected one named package directory")
    package = roots[0]
    manifest = json.loads((package / "manifest.json").read_text(encoding="utf-8-sig"))
    expected_paths = {"manifest.json"}
    for item in manifest["files"]:
        path = (package / item["path"]).resolve()
        if not path.is_relative_to(package):
            raise RuntimeError("Unsafe manifest path")
        if not path.is_file() or digest(path) != item["sha256"].lower():
            raise RuntimeError("Payload digest mismatch: " + item["path"])
        expected_paths.add(path.relative_to(package).as_posix())
    actual = {path.relative_to(package).as_posix() for path in package.rglob("*") if path.is_file()}
    if actual != expected_paths:
        raise RuntimeError("Manifest does not cover every payload file")
    settings = json.loads((package / "appsettings.json").read_text(encoding="utf-8-sig"))["App"]
    if any(settings[key] for key in ("ConnectionString", "JwtSecret", "StorageRoot")) or settings["Smtp"]["Password"]:
        raise RuntimeError("Packaged defaults contain usable private configuration")
    for path in package.rglob("*"):
        if path.is_file() and (path.name.lower() in ("appsettings.local.json", "secrets.json", ".env") or path.suffix.lower() in (".pfx", ".p12", ".key")):
            raise RuntimeError("Unexpected private file")
    clean_env = {key: value for key, value in os.environ.items() if not key.lower().startswith("app__") and key.upper() != "YF_CONFIG_PATH"}
    missing = subprocess.run(["dotnet", str(package / "Yf.Api.dll")], cwd=package, env=clean_env, capture_output=True, timeout=20)
    if missing.returncode == 0 or b"App:ConnectionString must be configured" not in missing.stderr:
        raise RuntimeError("Published application did not fail closed with missing configuration")
    print("PASS extracted ZIP paths, CRC, hashes, safe configuration and missing-config startup", flush=True)
    env = os.environ.copy()
    env["YF_TEST_API_DIR"] = str(package)
    run = subprocess.run([sys.executable, str(source / "server_dotnet/scripts/test-isolated.py")], env=env, cwd=source)
    if run.returncode:
        raise SystemExit(run.returncode)
    http_report = json.loads((source / ".runlogs/dotnet-published-results.json").read_text(encoding="utf-8"))
    report = {"archive": str(archive), "sha256": digest(archive), "bytes": archive.stat().st_size,
              "fileCount": len(actual), "source": manifest["source"], "build": manifest["build"],
              "zipPathsCrcAndHashes": "passed", "missingConfigurationFailsClosed": True,
              "publishedUnitHttp": http_report, "targetIisTested": False, "realSmtpTested": False}
    report_path.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print("Verified release report: " + str(report_path), flush=True)
