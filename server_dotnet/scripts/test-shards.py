"""Split the .NET test project into CI shards by test class.

`plan` balances the classes from a TRX result (for example the `verify-all-report`
artifact of a CI run) into N shards and writes tests/test-shards.json. `filter`
prints the `dotnet test --filter` expression for one shard.

Shards 1..N-1 run the classes listed for them. The last shard runs every class
that none of the other shards lists, so a new or renamed class can never be left
out: it lands in the last shard until the plan is regenerated.
"""

from __future__ import annotations

import argparse
import json
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
TESTS = ROOT / "server_dotnet" / "tests"
PLAN = TESTS / "test-shards.json"
TRX = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"
CLASS_NAME = re.compile(r"^[A-Za-z_][A-Za-z0-9_.]*$")


def duration_seconds(value: str) -> float:
    hours, minutes, seconds = value.split(":")
    return int(hours) * 3600 + int(minutes) * 60 + float(seconds)


def class_durations(trx: Path) -> dict[str, float]:
    root = ET.parse(trx).getroot()
    classes = {test.get("id"): test.find(TRX + "TestMethod").get("className")
               for test in root.iter(TRX + "UnitTest")}
    totals: dict[str, float] = {}
    for result in root.iter(TRX + "UnitTestResult"):
        name = classes[result.get("testId")]
        totals[name] = totals.get(name, 0.0) + duration_seconds(result.get("duration", "0:0:0"))
    return totals


def serial_classes() -> set[str]:
    """Classes in a collection with DisableParallelization run one after another, not on the thread pool."""
    serial_collections = set(re.findall(
        r'\[CollectionDefinition\(\s*"[^"]*"\s*,\s*DisableParallelization\s*=\s*true\s*\)\]\s*'
        r'(?:public|internal)?\s*(?:sealed\s+)?(?:static\s+)?class\s+(\w+)',
        "\n".join(path.read_text(encoding="utf-8-sig") for path in TESTS.rglob("*.cs")
                  if "bin" not in path.parts and "obj" not in path.parts)))
    found: set[str] = set()
    for path in TESTS.rglob("*.cs"):
        if "bin" in path.parts or "obj" in path.parts:
            continue
        source = path.read_text(encoding="utf-8-sig")
        namespace = re.search(r"namespace\s+([\w.]+)", source)
        for match in re.finditer(
                r"\[Collection\((\w+)\.Name\)\]\s*(?:\[[^\]]*\]\s*)*(?:public|internal)?\s*"
                r"(?:sealed\s+)?(?:partial\s+)?class\s+(\w+)", source):
            if match.group(1) in serial_collections and namespace:
                found.add(namespace.group(1) + "." + match.group(2))
    return found


def plan(trx_files: list[Path], shards: int, threads: int) -> dict:
    if shards < 2:
        raise SystemExit("--shards must be at least 2")
    durations: dict[str, float] = {}
    for trx in trx_files:
        for name, seconds in class_durations(trx).items():
            durations[name] = durations.get(name, 0.0) + seconds
    serial = serial_classes()
    # A parallel class shares the thread pool, so it costs about 1/threads of its time;
    # a serial class costs its full time. A shard also lasts at least as long as its slowest class.
    weighted = sorted(((seconds if name in serial else seconds / threads, seconds, name)
                       for name, seconds in durations.items()), reverse=True)
    bins = [{"classes": [], "load": 0.0, "longest": 0.0} for _ in range(shards)]
    for weight, seconds, name in weighted:
        target = min(bins, key=lambda item: max(item["load"] + weight, item["longest"], seconds))
        target["classes"].append(name)
        target["load"] += weight
        target["longest"] = max(target["longest"], seconds)
    return {
        "shards": shards,
        "generatedFrom": sorted(trx.name for trx in trx_files),
        "estimatedSeconds": [round(max(item["load"], item["longest"])) for item in bins],
        "classes": [sorted(item["classes"]) for item in bins],
    }


def class_filter(name: str) -> str:
    if not CLASS_NAME.match(name):
        raise SystemExit("unexpected test class name in plan: " + name)
    # The trailing dot keeps a class from also matching another class whose name merely starts with it.
    return name + "."


def shard_filter(shard: int, shards: int) -> str:
    data = json.loads(PLAN.read_text(encoding="utf-8"))
    if data["shards"] != shards:
        raise SystemExit(f"{PLAN.name} plans {data['shards']} shards, not {shards}; regenerate it with `plan`")
    if not 1 <= shard <= shards:
        raise SystemExit(f"shard must be between 1 and {shards}")
    if shard < shards:
        names = data["classes"][shard - 1]
        if not names:
            raise SystemExit(f"shard {shard} has no classes; regenerate the plan")
        return "|".join("FullyQualifiedName~" + class_filter(name) for name in names)
    listed = [name for names in data["classes"][:-1] for name in names]
    return "&".join("FullyQualifiedName!~" + class_filter(name) for name in listed)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    commands = parser.add_subparsers(dest="command", required=True)
    planner = commands.add_parser("plan", help="balance the classes of a TRX result into tests/test-shards.json")
    planner.add_argument("--trx", type=Path, nargs="+", required=True,
                         help="one TRX of a full run, or the TRX of every shard of a sharded run")
    planner.add_argument("--shards", type=int, required=True)
    planner.add_argument("--threads", type=int, default=6, help="maxParallelThreads in tests/xunit.runner.json")
    selector = commands.add_parser("filter", help="print the dotnet test --filter expression of one shard")
    selector.add_argument("--shard", type=int, required=True)
    selector.add_argument("--shards", type=int, required=True)
    args = parser.parse_args()
    if args.command == "plan":
        data = plan(args.trx, args.shards, args.threads)
        PLAN.write_text(json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")
        print(f"wrote {PLAN.relative_to(ROOT)}; estimated seconds per shard: {data['estimatedSeconds']}")
    else:
        sys.stdout.write(shard_filter(args.shard, args.shards))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
