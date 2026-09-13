import os
from pathlib import Path
import subprocess
import sys
import unittest


class MaintenanceOptimizationGuardTests(unittest.TestCase):
    def test_optimized_interpreters_are_rejected_before_database_import_or_configuration(self):
        script = Path(__file__).resolve().parent / "test-maintenance.py"
        base_environment = os.environ.copy()
        base_environment.pop("PYTHONOPTIMIZE", None)
        base_environment.pop("YF_TEST_DATABASE_URL", None)
        cases = [
            ([sys.executable, "-O", "-S", str(script)], base_environment),
            ([sys.executable, "-S", str(script)], {**base_environment, "PYTHONOPTIMIZE": "1"}),
        ]
        for command, environment in cases:
            with self.subTest(command=command, optimize=environment.get("PYTHONOPTIMIZE")):
                result = subprocess.run(command, env=environment, capture_output=True, text=True, timeout=10)
                output = result.stdout + result.stderr
                self.assertNotEqual(result.returncode, 0)
                self.assertIn("assertions disabled", output)
                self.assertNotIn("pymysql", output.lower())
                self.assertNotIn("YF_TEST_DATABASE_URL", output)


if __name__ == "__main__":
    unittest.main()
