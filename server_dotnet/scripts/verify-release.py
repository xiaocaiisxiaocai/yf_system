"""Validate and smoke-test a deployment directory or ZIP in owned temporary directories."""
import hashlib
import json
import ntpath
import os
import shutil
import stat
from pathlib import Path
import subprocess
import sys
import tempfile
import zipfile
from urllib.parse import urlparse
from xml.etree import ElementTree

from script_safety import (
    validate_release_sidecars,
    validate_zip_entries,
)
from test_first_start import verify_first_start

source = Path(__file__).resolve().parents[2]
archive = Path(sys.argv[1]).resolve()
directory_input = archive.is_dir()
artifacts = source / ".artifacts"
test_temp_root = artifacts / "tests" / "tmp"
report_root = artifacts / "reports" / "releases"
test_temp_root.mkdir(parents=True, exist_ok=True)
report_root.mkdir(parents=True, exist_ok=True)
report_path = report_root / ((archive.name if directory_input else archive.stem) + ".verification.json")
if report_path.exists():
    raise SystemExit("Verification report already exists; refusing to overwrite it")


def digest(path):
    with open(path, "rb") as file:
        return hashlib.file_digest(file, "sha256").hexdigest()


def validate_net8_runtime_config(path):
    runtime_options = json.loads(path.read_text(encoding="utf-8-sig")).get("runtimeOptions", {})
    if runtime_options.get("tfm") != "net8.0":
        raise RuntimeError("Published runtimeconfig does not target net8.0")
    frameworks = runtime_options.get("frameworks")
    if frameworks is None:
        framework = runtime_options.get("framework")
        frameworks = [framework] if framework else []
    versions = {item.get("name"): item.get("version", "") for item in frameworks if isinstance(item, dict)}
    for name in ("Microsoft.NETCore.App", "Microsoft.AspNetCore.App"):
        if not versions.get(name, "").startswith("8."):
            raise RuntimeError(f"Published runtimeconfig does not require {name} 8.x")
    return versions


def validate_precompressed_assets(package, actual):
    manifest_path = package / "precompressed-assets.json"
    public_root = package / "wwwroot"
    if not manifest_path.is_file() or not public_root.is_dir():
        raise RuntimeError("Precompressed asset manifest or public web root is missing")
    if (public_root / ".precompressed-assets.json").exists():
        raise RuntimeError("Precompression build metadata must not be publicly served")
    private_public_names = {"web.config", ".env", "secrets.json"}
    for path in public_root.rglob("*"):
        if not path.is_file():
            continue
        name = path.name.lower()
        if (name in private_public_names or name.startswith("appsettings")
                or name.endswith((".config", ".cs", ".deps.json", ".dll", ".pdb", ".pfx", ".p12", ".key",
                                  ".runtimeconfig.json"))):
            raise RuntimeError("Private configuration or executable file is exposed below wwwroot")
    verifier = source / "web/scripts/precompress-assets.mjs"
    run = subprocess.run(
        ["node", str(verifier), "--verify", str(public_root), str(manifest_path)],
        capture_output=True, text=True, errors="replace", timeout=180,
    )
    if run.returncode:
        detail = (run.stderr or run.stdout).strip().splitlines()
        raise RuntimeError("Precompressed asset verification failed: " + (detail[-1] if detail else "node verifier failed"))
    manifest = json.loads(manifest_path.read_text(encoding="utf-8-sig"))
    generated = []
    for asset in manifest.get("assets", []):
        source_path = "wwwroot/" + asset["path"]
        if source_path not in actual:
            raise RuntimeError("Precompression source is outside the packaged public payload")
        for encoding in ("br", "gzip"):
            representation = asset.get("encodings", {}).get(encoding, {})
            if representation.get("status") == "generated":
                packaged_path = "wwwroot/" + representation["path"]
                if packaged_path not in actual:
                    raise RuntimeError("Precompressed representation is missing from packaged payload")
                generated.append(packaged_path)
    return {"manifest": "precompressed-assets.json", "assets": len(manifest.get("assets", [])),
            "representations": len(generated), "decompressionByteExact": True,
            "publicBuildMetadataExcluded": True, "privatePublicFilesExcluded": True}


with tempfile.TemporaryDirectory(prefix="yf_dotnet_release_", dir=test_temp_root) as temp:
    extraction = Path(temp).resolve()
    if directory_input:
        # Verify an isolated copy, preserving the deployable source directory.
        if extraction.is_relative_to(archive):
            raise RuntimeError("Deployment directory cannot contain the verification workspace")
        for path in archive.rglob("*"):
            if path.is_symlink() or getattr(path.lstat(), "st_file_attributes", 0) & stat.FILE_ATTRIBUTE_REPARSE_POINT:
                raise RuntimeError("Linked paths are not allowed in a deployment directory")
        shutil.copytree(archive, extraction / archive.name)
    else:
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
        if not path.is_file() or digest(path) != item["sha256"].lower() or path.stat().st_size != item["bytes"]:
            raise RuntimeError("Payload digest mismatch: " + item["path"])
        expected_paths.add(path.relative_to(package).as_posix())
    actual = {path.relative_to(package).as_posix() for path in package.rglob("*") if path.is_file()}
    if actual != expected_paths:
        raise RuntimeError("Manifest does not cover every payload file")
    actual_files = {
        name: {"sha256": digest(package / name), "bytes": (package / name).stat().st_size}
        for name in sorted(actual)
    }
    release_manifest = None if directory_input else validate_release_sidecars(archive, manifest, actual_files)
    required = {"Yf.Api.dll", "Yf.Api.runtimeconfig.json", "web.config", "wwwroot/index.html", "precompressed-assets.json",
                "install-iis.ps1", "maintain-iis.ps1", "maintenance-common.ps1", "README.md"}
    if not required.issubset(actual):
        raise RuntimeError("Required application or maintenance payload is missing")
    if any(name.lower().startswith("clamav/") or "/oem" in name.lower()
           or name.lower() in {"install-clamav.ps1", "update-clamav.ps1", "clamav-database.ps1", "sharpcompress.dll"}
           for name in actual):
        raise RuntimeError("OEM or antivirus payload is still included")
    build = manifest.get("build", {})
    if (build.get("targetFramework") != "net8.0" or build.get("runtimeIdentifier") != "win-x64"
            or build.get("selfContained") is not False or not str(build.get("sdkVersion", "")).startswith("8.")):
        raise RuntimeError("Release manifest does not describe a .NET 8 win-x64 framework-dependent build")
    runtime_frameworks = validate_net8_runtime_config(package / "Yf.Api.runtimeconfig.json")
    precompressed_assets = validate_precompressed_assets(package, actual)
    if any("testhost" in Path(name).stem.lower() and Path(name).suffix.lower() in (".dll", ".exe") for name in actual):
        raise RuntimeError("A test host was included in the production payload")
    config_names = {name for name in actual if Path(name).name.lower().startswith("appsettings") and name.lower().endswith(".json")}
    if config_names != {"appsettings.json", "appsettings.Production.json"}:
        raise RuntimeError("Expected only base and Production configuration; no example or local settings")
    base_settings = json.loads((package / "appsettings.json").read_text(encoding="utf-8-sig"))
    if base_settings.get("App"):
        raise RuntimeError("Application configuration must exist only in Production settings")
    settings = json.loads((package / "appsettings.Production.json").read_text(encoding="utf-8-sig"))["App"]
    configuration = manifest.get("configuration")
    legacy_private_configuration = not isinstance(configuration, dict)
    if legacy_private_configuration:
        configuration = {"mode": "bundled-private", "containsSecrets": True, "inferredLegacy": True}
    configuration_mode = configuration.get("mode")
    if configuration_mode not in {"bundled-private", "external-template"}:
        raise RuntimeError("Release manifest has an unknown configuration mode")
    if settings.get("AutoInitializeDatabase") is not True:
        raise RuntimeError("Release must preserve the first-start contract")
    if configuration_mode == "bundled-private":
        if configuration.get("containsSecrets") is not True:
            raise RuntimeError("Private configuration mode is not marked secret-bearing")
        for key in ("ConnectionString", "JwtSecret", "StorageRoot", "WebBaseUrl", "BootstrapPassword"):
            if not settings.get(key):
                raise RuntimeError(f"Packaged private Production {key} is missing")
        if not 6 <= len(settings["BootstrapPassword"]) <= 20:
            raise RuntimeError("Private release bootstrap password has an invalid length")
        origin = urlparse(settings["WebBaseUrl"])
        if (origin.scheme not in ("http", "https") or not origin.hostname or origin.path not in ("", "/")
                or origin.params or origin.query or origin.fragment or origin.username
                or origin.hostname == "yf.example.com"):
            raise RuntimeError("Packaged WebBaseUrl is not the real site origin")
        if (origin.scheme == "http") == bool(settings.get("CookieSecure")):
            raise RuntimeError("Packaged CookieSecure does not match the site scheme")
        if len(settings["JwtSecret"].encode("utf-8")) < 32:
            raise RuntimeError("Packaged JWT secret is too short")
    else:
        if configuration.get("containsSecrets") is not False or any(
                settings.get(key) for key in ("ConnectionString", "JwtSecret", "BootstrapPassword")):
            raise RuntimeError("External configuration template contains bundled secrets")
    if not ntpath.isabs(settings["StorageRoot"]):
        raise RuntimeError("Packaged storage root must be an absolute Windows path")
    if any(key in settings for key in ("OemStorageRoot", "OemScanner")):
        raise RuntimeError("Packaged application still contains OEM settings")
    web_config = ElementTree.parse(package / "web.config")
    asp = web_config.find(".//aspNetCore")
    if asp is None or asp.get("processPath") != "dotnet" or asp.get("arguments") != r".\Yf.Api.dll" or asp.get("hostingModel") != "inprocess":
        raise RuntimeError("Published IIS launch configuration is inconsistent")
    environment = {node.get("name"): node.get("value") for node in asp.findall("environmentVariables/environmentVariable")}
    if any(environment.get(name) != "Production" for name in ("ASPNETCORE_ENVIRONMENT", "DOTNET_ENVIRONMENT")):
        raise RuntimeError("Published IIS configuration must select Production")
    for path in package.rglob("*"):
        if path.is_file() and (path.name.lower() in ("appsettings.local.json", "secrets.json", ".env") or path.suffix.lower() in (".pfx", ".p12", ".key")):
            raise RuntimeError("Unexpected private file")
    print("PASS deployment payload hashes, .NET 8 runtime and bundled IIS configuration", flush=True)
    first_start = verify_first_start(package, test_temp_root)
    env = os.environ.copy()
    env["ASPNETCORE_ENVIRONMENT"] = env["DOTNET_ENVIRONMENT"] = "Production"
    env["YF_TEST_API_DIR"] = str(package)
    published_report = Path(temp) / "dotnet-published-results.json"
    env["YF_TEST_RESULTS_PATH"] = str(published_report)
    run = subprocess.run([sys.executable, str(source / "server_dotnet/scripts/test-isolated.py")], env=env, cwd=source)
    if run.returncode:
        raise SystemExit(run.returncode)
    http_report = json.loads(published_report.read_text(encoding="utf-8"))
    report = {"input": str(archive), "inputType": "directory" if directory_input else "zip",
              "archive": None if directory_input else str(archive),
              "sha256": digest(package / "manifest.json") if directory_input else digest(archive),
              "sha256Target": "manifest.json" if directory_input else "archive",
              "bytes": sum(item["bytes"] for item in actual_files.values()) if directory_input else archive.stat().st_size,
              "fileCount": len(actual), "source": manifest["source"], "build": manifest["build"],
              "payloadHashes": "passed",
              "zipPathsCrcAndHashes": "not-applicable" if directory_input else "passed",
              "releaseSidecars": "not-applicable" if directory_input else "passed",
              "runtimeConfig": {"tfm": "net8.0", "frameworks": runtime_frameworks},
              "precompressedAssets": precompressed_assets,
              "releaseManifest": release_manifest, "configurationMode": configuration_mode,
              "legacyConfigurationModeInferred": legacy_private_configuration,
              "bundledConfigurationValidated": configuration_mode == "bundled-private",
              "externalConfigurationTemplateValidated": configuration_mode == "external-template",
              "testHostExcludedFromPayload": True,
              "publishedFirstStart": first_start,
              "publishedUnitHttp": http_report, "targetIisTested": False, "realSmtpTested": False}
    report_path.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print("Verified release report: " + str(report_path), flush=True)
