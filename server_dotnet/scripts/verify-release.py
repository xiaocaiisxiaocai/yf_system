"""Validate and smoke-test the exact distributed ZIP in owned temporary directories."""
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import zipfile

from script_safety import (
    clean_dotnet_config_environment,
    validate_release_sidecars,
    validate_zip_entries,
)

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
        validate_zip_entries(zipped.infolist())
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
    actual_files = {
        name: {"sha256": digest(package / name), "bytes": (package / name).stat().st_size}
        for name in sorted(actual)
    }
    release_manifest = validate_release_sidecars(archive, manifest, actual_files)
    required = {"Yf.Api.dll", "Yf.Api.runtimeconfig.json", "web.config", "wwwroot/index.html",
                "install-iis.ps1", "maintain-iis.ps1", "maintenance-common.ps1", "README.md"}
    if not required.issubset(actual):
        raise RuntimeError("Required application or maintenance payload is missing")
    if any("testhost" in Path(name).stem.lower() and Path(name).suffix.lower() in (".dll", ".exe") for name in actual):
        raise RuntimeError("A test host was included in the production payload")
    settings = json.loads((package / "appsettings.json").read_text(encoding="utf-8-sig"))["App"]
    if any(settings[key] for key in ("ConnectionString", "JwtSecret", "StorageRoot")) or settings["Smtp"]["Password"]:
        raise RuntimeError("Packaged defaults contain usable private configuration")
    for path in package.rglob("*"):
        if path.is_file() and (path.name.lower() in ("appsettings.local.json", "secrets.json", ".env") or path.suffix.lower() in (".pfx", ".p12", ".key")):
            raise RuntimeError("Unexpected private file")
    clean_env = clean_dotnet_config_environment()
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
              "zipPathsCrcAndHashes": "passed", "releaseSidecars": "passed",
              "releaseManifest": release_manifest, "missingConfigurationFailsClosed": True, "testHostExcludedFromPayload": True,
              "publishedUnitHttp": http_report, "targetIisTested": False, "realSmtpTested": False}
    report_path.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print("Verified release report: " + str(report_path), flush=True)
