"""Check automatic local publishing defaults without building or exposing secrets."""
import base64
import json
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / 'server_dotnet/scripts/publish-iis.ps1'
TEMP = ROOT / '.artifacts/tests/tmp'


class PublishDefaultsTests(unittest.TestCase):
    def invoke(self, path):
        quote = lambda value: "'" + str(value).replace("'", "''") + "'"
        command = (
            "$ErrorActionPreference='Stop';Set-StrictMode -Version 2.0;"
            "$t=$null;$e=$null;$ast=[Management.Automation.Language.Parser]::ParseFile("
            + quote(SCRIPT) + ",[ref]$t,[ref]$e);if($e.Count){throw 'Parse failed'};"
            "$ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]"
            " -and $n.Name -in @('Write-Utf8NoBom','Initialize-PublishDefaults')},$false)"
            "|ForEach-Object{Invoke-Expression $_.Extent.Text};"
            "$null=Initialize-PublishDefaults " + quote(path)
        )
        return subprocess.run(['powershell.exe', '-NoProfile', '-Command', command],
                              capture_output=True, text=True)

    def fixture(self, directory, connection='Server=127.0.0.1;User ID=fixture;Password=sample+secret', jwt=''):
        path = Path(directory) / 'publish-defaults.local.json'
        path.write_text(json.dumps({'App': {'ConnectionString': connection, 'JwtSecret': jwt}}), encoding='utf-8')
        return path

    def test_generates_strong_jwt_once_and_preserves_connection_and_key(self):
        TEMP.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=TEMP) as directory:
            path = self.fixture(directory)
            original = json.loads(path.read_text())['App']['ConnectionString']
            result = self.invoke(path)
            self.assertEqual(result.returncode, 0, 'default initialization failed')
            self.assertEqual(result.stdout.strip(), '', 'must not print credentials')
            first = path.read_bytes()
            app = json.loads(first)['App']
            self.assertEqual(len(base64.b64decode(app['JwtSecret'], validate=True)), 32)
            self.assertTrue(app['ConnectionString'] == original)
            self.assertEqual(self.invoke(path).returncode, 0)
            self.assertTrue(first == path.read_bytes(), 'repeated publishing must preserve defaults')

    def test_short_existing_key_is_rejected_without_rotation(self):
        TEMP.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=TEMP) as directory:
            path = self.fixture(directory, jwt='short')
            before = path.read_bytes()
            self.assertNotEqual(self.invoke(path).returncode, 0)
            self.assertTrue(before == path.read_bytes())

    def test_empty_connection_is_rejected_without_generating_key(self):
        TEMP.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=TEMP) as directory:
            path = self.fixture(directory, connection='')
            before = path.read_bytes()
            self.assertNotEqual(self.invoke(path).returncode, 0)
            self.assertTrue(before == path.read_bytes())


if __name__ == '__main__':
    unittest.main()
