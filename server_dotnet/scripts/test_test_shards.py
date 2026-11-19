"""Self-tests for scripts/test-shards.py: every test class runs in exactly one CI shard."""

import importlib.util
import json
import re
import unittest
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parent
ROOT = SCRIPTS.parents[1]
spec = importlib.util.spec_from_file_location("test_shards", SCRIPTS / "test-shards.py")
shards = importlib.util.module_from_spec(spec)
spec.loader.exec_module(shards)


def source_classes():
    """Test classes declared under server_dotnet/tests (any class that has [Fact] or [Theory] methods)."""
    found = set()
    for path in (ROOT / "server_dotnet/tests").rglob("*.cs"):
        if "bin" in path.parts or "obj" in path.parts:
            continue
        source = path.read_text(encoding="utf-8-sig")
        if not re.search(r"\[(Fact|Theory)\b", source):
            continue
        namespace = re.search(r"namespace\s+([\w.]+)", source)
        for match in re.finditer(r"(?:public|internal)\s+(?:sealed\s+)?(?:partial\s+)?class\s+(\w+)", source):
            found.add(namespace.group(1) + "." + match.group(1))
    return found


def matches(expression, name):
    """Evaluates the filter the way VSTest does for these shapes: `|` of contains, or `&` of not-contains."""
    test = name + ".SomeTest"
    if "!~" in expression:
        return all(part.split("!~", 1)[1] not in test for part in expression.split("&"))
    return any(part.split("~", 1)[1] in test for part in expression.split("|"))


class TestShardPlan(unittest.TestCase):
    def setUp(self):
        self.plan = json.loads(shards.PLAN.read_text(encoding="utf-8"))
        self.count = self.plan["shards"]

    def test_listed_classes_are_unique_and_valid(self):
        listed = [name for names in self.plan["classes"] for name in names]
        self.assertEqual(len(listed), len(set(listed)))
        for name in listed:
            self.assertRegex(name, shards.CLASS_NAME)

    def test_every_source_class_runs_in_exactly_one_shard(self):
        filters = [shards.shard_filter(index, self.count) for index in range(1, self.count + 1)]
        classes = source_classes()
        self.assertGreater(len(classes), 50)
        for name in classes:
            hits = [index + 1 for index, expression in enumerate(filters) if matches(expression, name)]
            self.assertEqual(1, len(hits), f"{name} runs in shards {hits}")

    def test_a_new_class_lands_in_the_last_shard(self):
        name = "Yf.Api.Tests.ClassThatDoesNotExistYet"
        filters = [shards.shard_filter(index, self.count) for index in range(1, self.count + 1)]
        self.assertEqual([self.count], [index + 1 for index, expression in enumerate(filters) if matches(expression, name)])

    def test_a_class_name_prefix_does_not_capture_a_longer_class(self):
        expression = "FullyQualifiedName~" + shards.class_filter("Yf.Api.Tests.FilesTests")
        self.assertFalse(matches(expression, "Yf.Api.Tests.FilesTestsExtra"))
        self.assertTrue(matches(expression, "Yf.Api.Tests.FilesTests"))

    def test_mismatched_shard_count_is_rejected(self):
        with self.assertRaises(SystemExit):
            shards.shard_filter(1, self.count + 1)


if __name__ == "__main__":
    unittest.main()
