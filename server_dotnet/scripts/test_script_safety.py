import hashlib
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
import warnings
import zipfile

sys.path.insert(0, str(Path(__file__).resolve().parent))
from script_safety import (
    clean_dotnet_config_environment,
    validate_release_sidecars,
    validate_zip_entries,
)

PROJECT_ROOT = Path(__file__).resolve().parents[2]
TEST_TEMP_ROOT = PROJECT_ROOT / ".artifacts" / "tests" / "tmp"


class ScriptSafetyTests(unittest.TestCase):
    def test_configuration_environment_is_removed_case_insensitively(self):
        cleaned = clean_dotnet_config_environment({
            "PATH": "tools",
            "YF_TEST_DATABASE_URL": "mysql://local",
            "App__ConnectionString": "wrong",
            "APP:StorageRoot": "wrong",
            "yf_config_path": "private.json",
            "Yf_Bootstrap_Password": "secret",
        })
        self.assertEqual(cleaned, {
            "PATH": "tools",
            "YF_TEST_DATABASE_URL": "mysql://local",
        })

    def test_zip_paths_must_be_unique_for_the_windows_target(self):
        validate_zip_entries([
            zipfile.ZipInfo("package/Yf.Api.dll"),
            zipfile.ZipInfo("package/wwwroot/index.html"),
        ])
        for names in (
            ("package/file.txt", "package/file.txt"),
            ("package/File.txt", "package/file.txt"),
            ("package/folder/", "package/folder"),
        ):
            with self.subTest(names=names):
                with self.assertRaisesRegex(RuntimeError, "Duplicate archive path"):
                    validate_zip_entries(map(zipfile.ZipInfo, names))

    def test_release_verifier_rejects_duplicate_members_before_extraction(self):
        TEST_TEMP_ROOT.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(prefix="yf_verify_duplicate_", dir=TEST_TEMP_ROOT) as directory:
            archive = Path(directory) / "duplicate.zip"
            with warnings.catch_warnings():
                warnings.simplefilter("ignore", UserWarning)
                with zipfile.ZipFile(archive, "w") as zipped:
                    zipped.writestr("package/file.txt", b"first")
                    zipped.writestr("package/file.txt", b"second")
            result = subprocess.run(
                [sys.executable, str(Path(__file__).with_name("verify-release.py")), str(archive)],
                capture_output=True,
                text=True,
                timeout=10,
            )
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("Duplicate archive path", result.stdout + result.stderr)

    def test_release_sidecars_match_archive_provenance_and_payload(self):
        TEST_TEMP_ROOT.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(prefix="yf_release_sidecars_", dir=TEST_TEMP_ROOT) as directory:
            root = Path(directory)
            archive = root / "release.zip"
            archive.write_bytes(b"release-archive")
            archive_hash = hashlib.sha256(archive.read_bytes()).hexdigest()
            package_manifest = {"source": {"gitHead": "abc"}, "build": {"configuration": "Release"}}
            actual_files = {"Yf.Api.dll": {"sha256": "123", "bytes": 10}}
            (root / "release.zip.sha256").write_text(
                f"{archive_hash}  {archive.name}\n", encoding="utf-8"
            )
            release_manifest = {
                **package_manifest,
                "files": [{"path": "Yf.Api.dll", "sha256": "123", "bytes": 10}],
                "archive": {"path": archive.name, "sha256": archive_hash, "bytes": archive.stat().st_size},
            }
            manifest_path = root / "release.release-manifest.json"
            manifest_path.write_text(json.dumps(release_manifest), encoding="utf-8")
            self.assertEqual(
                validate_release_sidecars(archive, package_manifest, actual_files),
                release_manifest,
            )

            (root / "release.zip.sha256").unlink()
            with self.assertRaisesRegex(RuntimeError, "sidecar is missing"):
                validate_release_sidecars(archive, package_manifest, actual_files)
            (root / "release.zip.sha256").write_text(
                f"{'0' * 64}  {archive.name}\n", encoding="utf-8"
            )
            with self.assertRaisesRegex(RuntimeError, "hash sidecar mismatch"):
                validate_release_sidecars(archive, package_manifest, actual_files)

            (root / "release.zip.sha256").write_text(
                f"{archive_hash}  {archive.name}\n", encoding="utf-8"
            )
            release_manifest["files"][0]["bytes"] = 11
            manifest_path.write_text(json.dumps(release_manifest), encoding="utf-8")
            with self.assertRaisesRegex(RuntimeError, "file manifest mismatch"):
                validate_release_sidecars(archive, package_manifest, actual_files)

    def test_release_directory_tampering_is_rejected_without_modifying_source(self):
        TEST_TEMP_ROOT.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(prefix="yf_verify_directory_", dir=TEST_TEMP_ROOT) as directory:
            package = Path(directory) / "directory-fixture"
            package.mkdir()
            payload = package / "Yf.Api.dll"
            payload.write_bytes(b"tampered fixture")
            (package / "manifest.json").write_text(json.dumps({"files": [
                {"path": "Yf.Api.dll", "sha256": "0" * 64, "bytes": payload.stat().st_size},
            ]}), encoding="utf-8")
            before = {p.name: p.read_bytes() for p in package.iterdir()}
            result = subprocess.run(
                [sys.executable, str(Path(__file__).with_name("verify-release.py")), str(package)],
                capture_output=True, text=True, timeout=10,
            )
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("Payload digest mismatch", result.stdout + result.stderr)
            self.assertEqual(before, {p.name: p.read_bytes() for p in package.iterdir()})


if __name__ == "__main__":
    unittest.main()
