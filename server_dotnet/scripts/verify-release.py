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
                "install-iis.ps1", "maintain-iis.ps1", "maintenance-common.ps1", "README.md",
                "install-clamav.ps1", "update-clamav.ps1", "clamav-database.ps1", "clamav/PROVENANCE.json",
                "clamav/clamd.conf.template", "clamav/freshclam.conf.template",
                "clamav/distribution/clamav-1.4.6.win.x64.zip", "clamav/distribution/clamav-1.4.6.tar.gz",
                "clamav/database-manifest.json", "clamav/database/main.cvd", "clamav/database/daily.cvd", "clamav/database/bytecode.cvd"}
    if not required.issubset(actual):
        raise RuntimeError("Required application or maintenance payload is missing")
    if any("testhost" in Path(name).stem.lower() and Path(name).suffix.lower() in (".dll", ".exe") for name in actual):
        raise RuntimeError("A test host was included in the production payload")
    settings = json.loads((package / "appsettings.json").read_text(encoding="utf-8-sig"))["App"]
    if settings.get("OemScanner", {}).get("Engine") != "ClamAV":
        raise RuntimeError("Published OEM scanner is not configured for ClamAV")
    clam_archive = package / "clamav/distribution/clamav-1.4.6.win.x64.zip"
    clam_digest = digest(clam_archive)
    if clam_digest != "57b6fd1d60cd87bafe800f97407ecdef0576d36b3900b8b7abcfbbabe88295fd":
        raise RuntimeError("ClamAV archive does not match the pinned upstream release")
    if digest(package / "clamav/distribution/clamav-1.4.6.tar.gz") != "06dbc7adf96e2f6a27c548a841ce83c95360d1e121b106a6f7cf56f282a3c61b":
        raise RuntimeError("ClamAV matching source archive is missing or changed")
    clam_runtime = extraction / "clamav-runtime"
    with zipfile.ZipFile(clam_archive) as nested:
        validate_zip_entries(nested.infolist())
        if nested.testzip() is not None:
            raise RuntimeError("ClamAV portable ZIP CRC failed")
        nested.extractall(clam_runtime)
    # clamd --version still requires clamd.conf; clamscan exposes the same
    # bundled engine version without requiring a configured database/service.
    clam_exe = list(clam_runtime.rglob("clamscan.exe"))
    if len(clam_exe) != 1:
        raise RuntimeError("Unexpected portable ClamAV layout")
    clam_version = subprocess.run([str(clam_exe[0]), "--version"], cwd=clam_exe[0].parent,
                                  capture_output=True, text=True, timeout=30, check=True)
    if not clam_version.stdout.strip().startswith("ClamAV 1.4.6"):
        raise RuntimeError("Packaged ClamAV executable did not report the pinned version")
    # Exercise the exact shared initializer used by install-clamav.ps1. It only
    # copies/verifies bundled files: no FreshClam, network download or service install.
    snapshot_check = extraction / "check-database.ps1"
    snapshot_check.write_text(
        "param($Helper,$Source,$Manifest,$Destination,$Sigtool)\n"
        "$ErrorActionPreference='Stop'\n. $Helper\n"
        "$snapshot = Copy-ClamAvDatabaseSnapshot -SourceDirectory $Source -ManifestPath $Manifest "
        "-DestinationDirectory $Destination -SigtoolPath $Sigtool\n"
        "$snapshot | ConvertTo-Json -Depth 8\n", encoding="utf-8")
    initialized_database = extraction / "installed-database"
    checked_database = subprocess.run([
        "powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(snapshot_check),
        "-Helper", str(package / "clamav-database.ps1"),
        "-Source", str(package / "clamav/database"), "-Manifest", str(package / "clamav/database-manifest.json"),
        "-Destination", str(initialized_database), "-Sigtool", str(clam_exe[0].parent / "sigtool.exe"),
    ], capture_output=True, text=True, timeout=180)
    if checked_database.returncode:
        raise RuntimeError("Bundled database initialization failed: " + checked_database.stderr[-2000:])
    snapshot = json.loads(checked_database.stdout.lstrip("\ufeff"))
    clean_sample = extraction / "clean-sample.txt"
    clean_sample.write_text("A harmless OEM scan smoke test.\n", encoding="ascii")
    eicar_archive = extraction / "eicar-smoke.zip"
    with zipfile.ZipFile(eicar_archive, "w") as test_archive:
        # EICAR is an intentionally harmless antivirus test string.
        test_archive.writestr("eicar.com", b"X5O!P%@AP[4\\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*")
    for sample, expected in ((clean_sample, 0), (eicar_archive, 1)):
        scanned = subprocess.run([str(clam_exe[0]), "--database=" + str(initialized_database),
                                  "--no-summary", "--stdout", str(sample)],
                                 cwd=clam_exe[0].parent, capture_output=True, text=True, timeout=180)
        if scanned.returncode != expected or (expected == 1 and "FOUND" not in scanned.stdout):
            raise RuntimeError("Offline bundled database scan failed: " + scanned.stdout[-1000:] + scanned.stderr[-1000:])
    print("PASS bundled signed database initialization and offline clean/EICAR archive scans", flush=True)
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
              "clamAvPayload": {"version": "1.4.6", "upstreamSha256": clam_digest,
                                "sourceArchiveHash": "passed", "extractedExecutableVersion": "passed",
                                "bundledDatabase": snapshot, "offlineInitializationAndScanning": "passed",
                                "installedWindowsServiceTested": False},
              "publishedUnitHttp": http_report, "targetIisTested": False, "realSmtpTested": False}
    report_path.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print("Verified release report: " + str(report_path), flush=True)
