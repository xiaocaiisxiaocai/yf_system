#!/usr/bin/env python3
"""Behavior tests for the signed ClamAV CVD snapshot helper."""

from __future__ import annotations

import hashlib
import json
import os
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path


SERVER_ROOT = Path(__file__).resolve().parents[1]
REPO_ROOT = SERVER_ROOT.parent
HELPER = SERVER_ROOT / "deploy" / "clamav-database.ps1"
REAL_DATABASE = REPO_ROOT / ".runlogs" / "clamav-runtime-smoke" / "database"
SIGTOOL = (
    REPO_ROOT
    / ".runlogs"
    / "clamav-runtime-smoke"
    / "clamav-1.4.6.win.x64"
    / "sigtool.exe"
)
NAMES = ("main.cvd", "daily.cvd", "bytecode.cvd")


def ps_quote(value: Path) -> str:
    return "'" + str(value).replace("'", "''") + "'"


def file_sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


class ClamAvDatabaseTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        if not SIGTOOL.is_file() or any(not (REAL_DATABASE / name).is_file() for name in NAMES):
            raise unittest.SkipTest("real ClamAV 1.4.6 sigtool/CVD fixtures are unavailable")
        runlogs = REPO_ROOT / ".runlogs"
        runlogs.mkdir(exist_ok=True)
        cls.temporary = tempfile.TemporaryDirectory(dir=runlogs)
        cls.root = Path(cls.temporary.name)
        cls.source = cls.root / "source"
        cls.source.mkdir()
        for name in NAMES:
            os.link(REAL_DATABASE / name, cls.source / name)
        result = cls.invoke(
            f"$snapshot=Get-ClamAvDatabaseSnapshot -DatabaseDirectory {ps_quote(cls.source)} "
            f"-SigtoolPath {ps_quote(SIGTOOL)};$snapshot|ConvertTo-Json -Depth 6"
        )
        if result.returncode != 0:
            raise AssertionError(result.stdout + result.stderr)
        cls.snapshot = json.loads(result.stdout)
        cls.manifest = cls.root / "database-manifest.json"
        cls.manifest.write_text(json.dumps(cls.snapshot, indent=2), encoding="utf-8")

    @classmethod
    def tearDownClass(cls) -> None:
        if hasattr(cls, "temporary"):
            cls.temporary.cleanup()

    @classmethod
    def invoke(cls, expression: str) -> subprocess.CompletedProcess[str]:
        command = f". {ps_quote(HELPER)};{expression}"
        return subprocess.run(
            ["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", command],
            check=False,
            capture_output=True,
            text=True,
        )

    def test_get_validates_real_official_three_cvd_snapshot(self) -> None:
        self.assertEqual(1, self.snapshot["schemaVersion"])
        self.assertEqual(list(NAMES), [item["name"] for item in self.snapshot["files"]])
        for item in self.snapshot["files"]:
            path = self.source / item["name"]
            self.assertEqual(path.stat().st_size, item["bytes"])
            self.assertEqual(file_sha256(path), item["sha256"])
            self.assertGreater(item["version"], 0)
            self.assertGreater(item["signatures"], 0)

    def test_copy_revalidates_snapshot_and_keeps_database_directory_strict(self) -> None:
        destination = self.root / "copied"
        result = self.invoke(
            f"$snapshot=Copy-ClamAvDatabaseSnapshot -SourceDirectory {ps_quote(self.source)} "
            f"-ManifestPath {ps_quote(self.manifest)} -DestinationDirectory {ps_quote(destination)} "
            f"-SigtoolPath {ps_quote(SIGTOOL)};$snapshot|ConvertTo-Json -Depth 6"
        )
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        copied = json.loads(result.stdout)
        self.assertEqual(self.snapshot, copied)
        self.assertEqual(set(NAMES), {path.name for path in destination.iterdir()})

    def test_tampered_cvd_is_rejected(self) -> None:
        source = self.root / "tampered"
        source.mkdir()
        os.link(REAL_DATABASE / "main.cvd", source / "main.cvd")
        os.link(REAL_DATABASE / "daily.cvd", source / "daily.cvd")
        shutil.copyfile(REAL_DATABASE / "bytecode.cvd", source / "bytecode.cvd")
        with (source / "bytecode.cvd").open("r+b") as stream:
            stream.seek(-1, os.SEEK_END)
            byte = stream.read(1)
            stream.seek(-1, os.SEEK_END)
            stream.write(bytes([byte[0] ^ 0x01]))
        result = self.invoke(
            f"Get-ClamAvDatabaseSnapshot -DatabaseDirectory {ps_quote(source)} -SigtoolPath {ps_quote(SIGTOOL)}"
        )
        self.assertNotEqual(0, result.returncode)

    def test_missing_cvd_is_rejected(self) -> None:
        source = self.root / "missing"
        source.mkdir()
        os.link(REAL_DATABASE / "main.cvd", source / "main.cvd")
        os.link(REAL_DATABASE / "daily.cvd", source / "daily.cvd")
        result = self.invoke(
            f"Get-ClamAvDatabaseSnapshot -DatabaseDirectory {ps_quote(source)} -SigtoolPath {ps_quote(SIGTOOL)}"
        )
        self.assertNotEqual(0, result.returncode)

    def test_manifest_mismatch_does_not_create_destination(self) -> None:
        manifest = self.root / "bad-manifest.json"
        content = json.loads(json.dumps(self.snapshot))
        content["files"][0]["sha256"] = "0" * 64
        manifest.write_text(json.dumps(content), encoding="utf-8")
        destination = self.root / "manifest-mismatch-target"
        result = self.invoke(
            f"Copy-ClamAvDatabaseSnapshot -SourceDirectory {ps_quote(self.source)} "
            f"-ManifestPath {ps_quote(manifest)} -DestinationDirectory {ps_quote(destination)} "
            f"-SigtoolPath {ps_quote(SIGTOOL)}"
        )
        self.assertNotEqual(0, result.returncode)
        self.assertFalse(destination.exists())

    def test_nonempty_destination_is_rejected_without_modification(self) -> None:
        destination = self.root / "nonempty"
        destination.mkdir()
        sentinel = destination / "keep.txt"
        sentinel.write_text("keep", encoding="utf-8")
        result = self.invoke(
            f"Copy-ClamAvDatabaseSnapshot -SourceDirectory {ps_quote(self.source)} "
            f"-ManifestPath {ps_quote(self.manifest)} -DestinationDirectory {ps_quote(destination)} "
            f"-SigtoolPath {ps_quote(SIGTOOL)}"
        )
        self.assertNotEqual(0, result.returncode)
        self.assertEqual("keep", sentinel.read_text(encoding="utf-8"))
        self.assertEqual([sentinel], list(destination.iterdir()))

    def test_contained_destination_is_rejected_without_changing_source(self) -> None:
        before = {path.name: file_sha256(path) for path in self.source.iterdir()}
        nested_destination = self.source / "nested-target"
        result = self.invoke(
            f"Copy-ClamAvDatabaseSnapshot -SourceDirectory {ps_quote(self.source)} "
            f"-ManifestPath {ps_quote(self.manifest)} -DestinationDirectory {ps_quote(nested_destination)} "
            f"-SigtoolPath {ps_quote(SIGTOOL)}"
        )
        self.assertNotEqual(0, result.returncode)
        self.assertFalse(nested_destination.exists())
        self.assertEqual(before, {path.name: file_sha256(path) for path in self.source.iterdir()})

        sentinel = self.root / "container-sentinel.txt"
        sentinel.write_text("keep", encoding="utf-8")
        result = self.invoke(
            f"Copy-ClamAvDatabaseSnapshot -SourceDirectory {ps_quote(self.source)} "
            f"-ManifestPath {ps_quote(self.manifest)} -DestinationDirectory {ps_quote(self.root)} "
            f"-SigtoolPath {ps_quote(SIGTOOL)}"
        )
        self.assertNotEqual(0, result.returncode)
        self.assertEqual("keep", sentinel.read_text(encoding="utf-8"))


if __name__ == "__main__":
    unittest.main(verbosity=2)
