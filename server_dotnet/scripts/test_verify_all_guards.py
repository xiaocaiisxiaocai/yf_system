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


class VerificationConfigurationContracts(unittest.TestCase):
    def test_web_quality_configuration_is_explicit(self):
        web = ROOT / "web"
        package = json.loads((web / "package.json").read_text(encoding="utf-8"))
        lint = package["scripts"]["lint"]
        self.assertIn("--deny-warnings", lint)
        bundled = {"dayjs", "exceljs", "lodash", "pptx-preview", "tinycolor2", "xlsx"}
        self.assertTrue(bundled.issubset(package["dependencies"]))
        self.assertEqual(
            bundled,
            set(package["yfDependencyPolicy"]["bundledPreviewRuntimeDependencies"]),
        )
        self.assertIn("bundled into production", package["yfDependencyPolicy"]["reason"])

        oxlint = json.loads((web / ".oxlintrc.json").read_text(encoding="utf-8"))
        self.assertEqual("error", oxlint["rules"]["react-hooks/exhaustive-deps"])
        self.assertEqual("error", oxlint["rules"]["react/no-array-index-key"])
        for name in ("tsconfig.app.json", "tsconfig.node.json"):
            config = (web / name).read_text(encoding="utf-8")
            self.assertRegex(config, r'"strict"\s*:\s*true')

        vite = (web / "vite.config.ts").read_text(encoding="utf-8")
        self.assertIn("chunkSizeWarningLimit: 600", vite)
        self.assertIn("knownLargeChunks", vite)
        self.assertIn("Unexpected JavaScript chunk exceeds 600 KiB", vite)

    def test_development_scripts_keep_credentials_out_of_environment_and_handle_native_stderr(self):
        scripts = ROOT / "server_dotnet/scripts"
        restart = (scripts / "restart-dev.ps1").read_text(encoding="utf-8-sig")
        self.assertNotIn("MYSQL_PWD", restart)
        self.assertIn("--defaults-file=", restart)
        self.assertIn("@('user id', 'user', 'uid')", restart)
        self.assertLess(restart.index("$buildExit ="), restart.index("Stopping backend pid"))
        check_dev = (scripts / "check-dev.ps1").read_text(encoding="utf-8-sig")
        self.assertIn("$ErrorActionPreference = 'Continue'", check_dev)
        self.assertIn("$buildExitCode = $LASTEXITCODE", check_dev)

    def test_full_gate_declares_all_non_browser_checks(self):
        source = SCRIPT.read_text(encoding="utf-8-sig")
        for gate in (
            "dotnet-tool-restore",
            "python-script-self-tests",
            "npm-test-precompression",
        ):
            self.assertIn("'" + gate + "'", source)
        self.assertIn("[switch]$AllowSkips", source)
        self.assertIn("if (-not $AllowSkips -and -not $fullCoverage)", source)

    def test_http_runner_records_probe_failures_and_bypasses_proxies(self):
        source = (ROOT / "server_dotnet/scripts/test-isolated.py").read_text(encoding="utf-8")
        self.assertIn("urllib.request.ProxyHandler({})", source)
        self.assertIn("lastProbe={last_failure}", source)
        self.assertIn("target={target}", source)
        self.assertIn('"ASPNETCORE_URLS": base', source)
        self.assertNotIn('"URLS": base', source)
        self.assertIn('["dotnet", str(DLL), "--urls", base]', source)


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

    def test_missing_database_fails_closed_before_restore(self):
        env = os.environ.copy()
        env.pop("YF_TEST_DATABASE_URL", None)
        env.pop("YF_UPDATE_OPENAPI", None)
        run = subprocess.run(
            ["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(SCRIPT)],
            env=env,
            cwd=ROOT,
            capture_output=True,
            text=True,
            errors="replace",
            timeout=30,
        )
        self.assertEqual(1, run.returncode, run.stdout + run.stderr)
        match = re.search(r"Report: (.+summary\.json)", run.stdout)
        self.assertIsNotNone(match, run.stdout + run.stderr)
        report = json.loads(Path(match.group(1).strip()).read_text(encoding="utf-8-sig"))
        self.assertEqual("failed", report["status"])
        self.assertFalse(report["fullCoverage"])
        self.assertFalse(report["allowSkips"])
        self.assertEqual(
            "skipped",
            next(row for row in report["steps"] if row["name"] == "database-configuration")["status"],
        )
        self.assertEqual(
            "skipped",
            next(row for row in report["steps"] if row["name"] == "restore-api")["status"],
        )


if __name__ == "__main__":
    unittest.main()
