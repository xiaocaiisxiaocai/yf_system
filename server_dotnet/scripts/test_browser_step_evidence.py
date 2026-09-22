import json
from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parent))
from browser_step_evidence import (
    BrowserStepEvidenceError,
    snapshot_browser_evidence,
    validate_browser_step_evidence,
)

TEST_TEMP_ROOT = Path(__file__).resolve().parents[2] / ".artifacts" / "tests" / "tmp"
TEST_TEMP_ROOT.mkdir(parents=True, exist_ok=True)


class BrowserStepEvidenceTests(unittest.TestCase):
    def test_empty_step_is_rejected(self):
        with tempfile.TemporaryDirectory(prefix="yf_browser_evidence_", dir=TEST_TEMP_ROOT) as temporary:
            output = Path(temporary)
            before = snapshot_browser_evidence(output)
            after = snapshot_browser_evidence(output)
            with self.assertRaisesRegex(BrowserStepEvidenceError, "produced no new operation"):
                validate_browser_step_evidence("system", before, after, output)

    def test_failed_step_evidence_is_rejected(self):
        with tempfile.TemporaryDirectory(prefix="yf_browser_evidence_", dir=TEST_TEMP_ROOT) as temporary:
            output = Path(temporary)
            before = snapshot_browser_evidence(output)
            (output / "browser-operations.json").write_text(
                json.dumps([{"name": "failure", "status": "fail"}]), encoding="utf-8"
            )
            after = snapshot_browser_evidence(output)
            with self.assertRaisesRegex(BrowserStepEvidenceError, "non-passing operation"):
                validate_browser_step_evidence("system", before, after, output)

    def test_successful_step_reports_only_new_operations(self):
        with tempfile.TemporaryDirectory(prefix="yf_browser_evidence_", dir=TEST_TEMP_ROOT) as temporary:
            output = Path(temporary)
            operation_file = output / "browser-operations.json"
            operation_file.write_text(json.dumps([{"name": "earlier", "status": "pass"}]), encoding="utf-8")
            before = snapshot_browser_evidence(output)
            operation_file.write_text(json.dumps([
                {"name": "earlier", "status": "pass"},
                {"name": "current", "status": "pass"},
            ]), encoding="utf-8")
            after = snapshot_browser_evidence(output)
            self.assertEqual(
                validate_browser_step_evidence("system", before, after, output),
                {"step": "system", "kind": "operations", "added": 1},
            )

    def test_auth_and_fixture_steps_require_their_own_complete_evidence(self):
        with tempfile.TemporaryDirectory(prefix="yf_browser_evidence_", dir=TEST_TEMP_ROOT) as temporary:
            output = Path(temporary)
            before_auth = snapshot_browser_evidence(output)
            (output / "browser-results.json").write_text(
                json.dumps({"checks": [{"name": "login", "status": "pass"}]}), encoding="utf-8"
            )
            after_auth = snapshot_browser_evidence(output)
            self.assertEqual(validate_browser_step_evidence("auth", before_auth, after_auth, output)["added"], 1)

            before_fixtures = snapshot_browser_evidence(output)
            fixtures = {
                "roles": {"内部成员": 1, "项目管理员": 2},
                "suppliers": {"a": {"id": 3, "name": "甲"}, "b": {"id": 4, "name": "乙"}},
                "users": {
                    key: {"id": index, "username": key, "password": "initial", "changedPassword": "changed"}
                    for index, key in enumerate(("a", "b", "member", "manager"), start=5)
                },
            }
            (output / "fixtures.private.json").write_text(json.dumps(fixtures), encoding="utf-8")
            (output / "valid-preview.pdf").write_bytes(b"pdf")
            (output / "vendor-response.xlsx").write_bytes(b"xlsx")
            after_fixtures = snapshot_browser_evidence(output)
            result = validate_browser_step_evidence("fixtures", before_fixtures, after_fixtures, output)
            self.assertEqual(result["added"], 1)
            self.assertEqual(set(result["sampleSizes"]), {"valid-preview.pdf", "vendor-response.xlsx"})


if __name__ == "__main__":
    unittest.main()
