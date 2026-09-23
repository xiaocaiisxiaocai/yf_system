"""Black-box safety/reporting checks; reject before any restore, build or database access."""
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "server_dotnet/scripts/verify-all.ps1"


@unittest.skipUnless(os.name == "nt", "Windows PowerShell entry point")
class VerifyAllGuards(unittest.TestCase):
    def reject(self, overrides, step):
        env = os.environ.copy()
        env.pop("YF_TEST_DATABASE_URL", None)
        env.pop("YF_UPDATE_OPENAPI", None)
        env.update(overrides)
        temp_root = ROOT / ".artifacts/tests/tmp"
        temp_root.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(prefix="yf_verify_guard_", dir=temp_root) as cwd:
            run = subprocess.run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(SCRIPT)],
                env=env, cwd=cwd, capture_output=True, text=True, errors="replace", timeout=30)
        self.assertEqual(1, run.returncode, run.stdout + run.stderr)
        self.assertNotIn("Argument types do not match", run.stderr)
        match = re.search(r"Report: (.+summary\.json)", run.stdout)
        self.assertIsNotNone(match, run.stdout + run.stderr)
        report = json.loads(Path(match.group(1).strip()).read_text(encoding="utf-8-sig"))
        self.assertEqual("failed", report["status"])
        self.assertFalse(report["fullCoverage"])
        self.assertEqual("failed", next(row for row in report["steps"] if row["name"] == step)["status"])
        for row in report["steps"]:
            if row["name"].startswith(("restore-", "build-", "npm-")):
                self.assertEqual("skipped", row["status"])

    def test_verification_cannot_rewrite_openapi_snapshot(self):
        snapshot = ROOT / "server_dotnet/tests/Contracts/openapi-v1.json"
        before = hashlib.sha256(snapshot.read_bytes()).digest()
        self.reject({"YF_UPDATE_OPENAPI": "1"}, "openapi-update-guard")
        self.assertEqual(before, hashlib.sha256(snapshot.read_bytes()).digest())

    def test_remote_test_database_is_rejected_with_failure_report(self):
        self.reject({"YF_TEST_DATABASE_URL": "mysql://fixture:synthetic-only@db.example.invalid:3306/ignored"},
                    "database-configuration")


if __name__ == "__main__":
    unittest.main()
