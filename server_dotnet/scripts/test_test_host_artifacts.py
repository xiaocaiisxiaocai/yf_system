import tempfile
import unittest
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parent))
from test_host_artifacts import TestHostArtifactError, verify_test_host_artifacts


class TestHostArtifactTests(unittest.TestCase):
    def fixture(self):
        temporary = tempfile.TemporaryDirectory(prefix="yf_test_host_artifacts_")
        root = Path(temporary.name)
        api = root / "api"
        host = root / "host"
        api.mkdir()
        host.mkdir()
        for name, payload in {
            "Yf.Api.dll": b"current-api",
            "Dependency.dll": b"current-dependency",
        }.items():
            (api / name).write_bytes(payload)
            (host / name).write_bytes(payload)
        entry = host / "Yf.Api.TestHost.dll"
        entry.write_bytes(b"test-host")
        (host / "Yf.Api.TestHost.deps.json").write_text("{}", encoding="utf-8")
        (host / "Yf.Api.TestHost.runtimeconfig.json").write_text("{}", encoding="utf-8")
        return temporary, api, entry

    def test_matching_payload_returns_actual_loaded_artifact_hashes(self):
        temporary, api, entry = self.fixture()
        with temporary:
            result = verify_test_host_artifacts(api, entry)
            self.assertEqual(
                {path.name for path in result},
                {
                    "Yf.Api.dll",
                    "Dependency.dll",
                    "Yf.Api.TestHost.dll",
                    "Yf.Api.TestHost.deps.json",
                    "Yf.Api.TestHost.runtimeconfig.json",
                },
            )

    def test_stale_api_copy_is_rejected(self):
        temporary, api, entry = self.fixture()
        with temporary:
            (entry.parent / "Yf.Api.dll").write_bytes(b"stale-api")
            with self.assertRaisesRegex(TestHostArtifactError, "Yf.Api.dll"):
                verify_test_host_artifacts(api, entry)

    def test_stale_or_missing_dependency_is_rejected(self):
        temporary, api, entry = self.fixture()
        with temporary:
            dependency = entry.parent / "Dependency.dll"
            dependency.write_bytes(b"stale-dependency")
            with self.assertRaisesRegex(TestHostArtifactError, "Dependency.dll"):
                verify_test_host_artifacts(api, entry)
            dependency.unlink()
            with self.assertRaisesRegex(TestHostArtifactError, "missing: Dependency.dll"):
                verify_test_host_artifacts(api, entry)


if __name__ == "__main__":
    unittest.main()
