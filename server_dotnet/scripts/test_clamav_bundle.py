#!/usr/bin/env python3
"""Static and cached-asset checks for the pinned ClamAV release bundle."""

from __future__ import annotations

import hashlib
import json
import os
import subprocess
import tempfile
import unittest
import zipfile
from pathlib import Path


SERVER_ROOT = Path(__file__).resolve().parents[1]
REPO_ROOT = SERVER_ROOT.parent
ARTIFACTS_ROOT = REPO_ROOT / ".artifacts"
TEST_TEMP_ROOT = ARTIFACTS_ROOT / "tests" / "tmp"
CACHE_ROOT = Path(os.environ.get("CLAMAV_CACHE_DIR", ARTIFACTS_ROOT / "cache" / "clamav"))
BUNDLE_ROOT = Path(os.environ.get("CLAMAV_BUNDLE_ROOT", ARTIFACTS_ROOT / "tests" / "clamav" / "bundle-stage-146"))
BINARY_NAME = "clamav-1.4.6.win.x64.zip"
BINARY_SIZE = 191_947_200
BINARY_SHA256 = "57b6fd1d60cd87bafe800f97407ecdef0576d36b3900b8b7abcfbbabe88295fd"


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


class ClamAvBundleTests(unittest.TestCase):
    def run_prepare(self, output: Path, cache: Path) -> subprocess.CompletedProcess[str]:
        return subprocess.run(
            [
                "powershell.exe",
                "-NoProfile",
                "-ExecutionPolicy",
                "Bypass",
                "-File",
                str(SERVER_ROOT / "scripts" / "prepare-clamav.ps1"),
                "-OutputDirectory",
                str(output),
                "-CacheDirectory",
                str(cache),
                "-UseExistingCacheOnly",
            ],
            check=False,
            capture_output=True,
            text=True,
        )

    def test_powershell_files_parse(self) -> None:
        files = [
            SERVER_ROOT / "scripts" / "prepare-clamav.ps1",
            SERVER_ROOT / "scripts" / "publish-iis.ps1",
            SERVER_ROOT / "deploy" / "install-clamav.ps1",
            SERVER_ROOT / "deploy" / "update-clamav.ps1",
            SERVER_ROOT / "deploy" / "clamav-database.ps1",
            SERVER_ROOT / "deploy" / "install-iis.ps1",
        ]
        command = (
            "$failed=$false;"
            + "foreach($p in @(" + ",".join("'" + str(p).replace("'", "''") + "'" for p in files) + ")){"
            "$t=$null;$e=$null;[void][Management.Automation.Language.Parser]::ParseFile($p,[ref]$t,[ref]$e);"
            "if($e.Count){$e|ForEach-Object{$_.Message};$failed=$true}};if($failed){exit 1}"
        )
        completed = subprocess.run(
            ["powershell.exe", "-NoProfile", "-Command", command],
            check=False,
            capture_output=True,
            text=True,
        )
        self.assertEqual(0, completed.returncode, completed.stdout + completed.stderr)

    def test_application_example_matches_bundled_endpoint(self) -> None:
        settings = json.loads((SERVER_ROOT / "deploy" / "appsettings.example.json").read_text(encoding="utf-8"))
        scanner = settings["App"]["OemScanner"]
        self.assertEqual("ClamAV", scanner["Engine"])
        self.assertEqual(
            {
                "Host": "127.0.0.1",
                "Port": 3310,
                "ConnectTimeoutSeconds": 5,
                "MaxStreamBytes": 1_073_741_824,
            },
            scanner["ClamAv"],
        )

    def test_prepare_refuses_existing_output_without_touching_it(self) -> None:
        TEST_TEMP_ROOT.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=TEST_TEMP_ROOT) as temporary:
            root = Path(temporary)
            output = root / "existing-output"
            output.mkdir()
            sentinel = output / "sentinel.txt"
            sentinel.write_text("keep", encoding="utf-8")
            completed = self.run_prepare(output, root / "unused-cache")
            self.assertNotEqual(0, completed.returncode)
            self.assertEqual("keep", sentinel.read_text(encoding="utf-8"))
            self.assertEqual([sentinel], list(output.iterdir()))

    def test_prepare_rejects_corrupt_offline_cache_without_output(self) -> None:
        TEST_TEMP_ROOT.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=TEST_TEMP_ROOT) as temporary:
            root = Path(temporary)
            cache = root / "cache"
            cache.mkdir()
            (cache / BINARY_NAME).write_bytes(b"not the official archive")
            output = root / "output"
            completed = self.run_prepare(output, cache)
            self.assertNotEqual(0, completed.returncode)
            self.assertFalse(output.exists())

    def test_prepare_rejects_output_outside_project_artifacts(self) -> None:
        external = Path(tempfile.gettempdir()) / ("yf-external-output-" + os.urandom(8).hex())
        completed = self.run_prepare(external, CACHE_ROOT)
        self.assertNotEqual(0, completed.returncode)
        self.assertIn("project artifact root", completed.stdout + completed.stderr)
        self.assertFalse(external.exists())

    def test_publish_rejects_output_outside_project_artifacts_before_build(self) -> None:
        external = Path(tempfile.gettempdir()) / ("yf-external-release-" + os.urandom(8).hex())
        completed = subprocess.run(
            [
                "powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                str(SERVER_ROOT / "scripts" / "publish-iis.ps1"),
                "-FreshOutputDirectory", str(external),
                "-UseExistingClamAvCacheOnly",
            ],
            check=False,
            capture_output=True,
            text=True,
        )
        self.assertNotEqual(0, completed.returncode)
        self.assertIn("project artifact root", completed.stdout + completed.stderr)
        self.assertFalse(external.exists())

    def test_cached_official_archive_when_available(self) -> None:
        archive = CACHE_ROOT / BINARY_NAME
        if not archive.is_file():
            self.skipTest(f"official cache not present: {archive}")
        self.assertEqual(BINARY_SIZE, archive.stat().st_size)
        self.assertEqual(BINARY_SHA256, sha256(archive))
        with zipfile.ZipFile(archive) as package:
            names = [name.replace("\\", "/") for name in package.namelist()]
        for leaf in ("clamd.exe", "freshclam.exe", "clamdscan.exe", "COPYING.txt", "README.md"):
            self.assertTrue(any(name.endswith("/" + leaf) for name in names), leaf)
        self.assertTrue(any("/COPYING/COPYING." in name for name in names))

    def test_prepared_payload_when_available(self) -> None:
        provenance_path = BUNDLE_ROOT / "PROVENANCE.json"
        if not provenance_path.is_file():
            self.skipTest(f"prepared payload not present: {BUNDLE_ROOT}")
        provenance = json.loads(provenance_path.read_text(encoding="utf-8"))
        self.assertEqual("1.4.6", provenance["version"])
        self.assertEqual("Windows x64", provenance["platform"])
        self.assertEqual(
            "distribution/clamav-1.4.6.tar.gz",
            provenance["completeCorrespondingSource"],
        )
        binary = BUNDLE_ROOT / "distribution" / BINARY_NAME
        self.assertEqual(BINARY_SHA256, sha256(binary))
        snapshot = json.loads((BUNDLE_ROOT / "database-manifest.json").read_text(encoding="utf-8-sig"))
        self.assertEqual(1, snapshot["schemaVersion"])
        self.assertEqual({"main.cvd", "daily.cvd", "bytecode.cvd"}, {item["name"] for item in snapshot["files"]})
        for item in snapshot["files"]:
            file = BUNDLE_ROOT / "database" / item["name"]
            self.assertEqual(item["bytes"], file.stat().st_size)
            self.assertEqual(item["sha256"], sha256(file))


if __name__ == "__main__":
    unittest.main(verbosity=2)
