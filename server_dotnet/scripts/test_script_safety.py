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

    def test_zip_paths_reject_names_windows_cannot_represent(self):
        validate_zip_entries([
            zipfile.ZipInfo("package/console.txt"),
            zipfile.ZipInfo("package/CONFIG/nulls.json"),
            zipfile.ZipInfo("package/LPT10.txt"),
            zipfile.ZipInfo("package/.well-known/file"),
            zipfile.ZipInfo("package/dir/"),
        ])
        for name in (
            "package/file.",
            "package/file ",
            "package/folder./file.txt",
            "package/folder /file.txt",
            "package/CON",
            "package/con.txt",
            "package/Nul.tar.gz",
            "package/AUX/file.txt",
            "package/PRN",
            "package/COM1",
            "package/com9.log",
            "package/LPT1",
            "package/lpt9.txt",
            "package/CON .txt",
            "package/a<b",
            "package/a>b",
            "package/a\"b",
            "package/a|b",
            "package/a?b",
            "package/a*b",
            "package/a\x01b",
            "package/a:b",
            "package//double",
            "/absolute.txt",
            "package/../escape.txt",
        ):
            with self.subTest(name=name):
                with self.assertRaisesRegex(RuntimeError, "Unsafe archive path"):
                    validate_zip_entries([zipfile.ZipInfo(name)])

    def test_zip_total_size_and_entry_count_are_bounded(self):
        def sized(name, size):
            info = zipfile.ZipInfo(name)
            info.file_size = size
            return info

        validate_zip_entries([sized("package/a.bin", 60), sized("package/b.bin", 40)], max_total_bytes=100)
        with self.assertRaisesRegex(RuntimeError, "uncompressed size limit"):
            validate_zip_entries([sized("package/a.bin", 60), sized("package/b.bin", 41)], max_total_bytes=100)
        validate_zip_entries([zipfile.ZipInfo(f"package/{index}.txt") for index in range(3)], max_entries=3)
        with self.assertRaisesRegex(RuntimeError, "too many entries"):
            validate_zip_entries([zipfile.ZipInfo(f"package/{index}.txt") for index in range(4)], max_entries=3)

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

    def test_release_verifier_requires_third_party_license_payload(self):
        TEST_TEMP_ROOT.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(prefix="yf_verify_licenses_", dir=TEST_TEMP_ROOT) as directory:
            package = Path(directory) / "license-fixture"
            present = ["Yf.Api.dll", "Yf.Api.runtimeconfig.json", "web.config", "wwwroot/index.html",
                       "precompressed-assets.json", "install-iis.ps1", "maintain-iis.ps1",
                       "maintenance-common.ps1", "README.md", "SharpCompress.dll",
                       "licenses/SharpCompress-LICENSE.txt"]
            entries = []
            for name in present:
                path = package / name
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(name.encode("utf-8"))
                entries.append({"path": name, "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
                                "bytes": path.stat().st_size})
            (package / "manifest.json").write_text(json.dumps({"files": entries}), encoding="utf-8")
            result = subprocess.run(
                [sys.executable, str(Path(__file__).with_name("verify-release.py")), str(package)],
                capture_output=True, text=True, timeout=10,
            )
            output = result.stdout + result.stderr
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("license payload is missing", output)
            for name in ("THIRD-PARTY-NOTICES.md", "licenses/Apache-2.0.txt",
                         "licenses/Apache-Commons-Compress-NOTICE.txt"):
                self.assertIn(name, output)
            self.assertNotIn("licenses/SharpCompress-LICENSE.txt", output)


if __name__ == "__main__":
    unittest.main()
