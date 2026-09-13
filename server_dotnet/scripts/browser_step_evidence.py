"""Evidence checks for independently executed browser acceptance steps."""

from __future__ import annotations

import json
from pathlib import Path
from typing import Any


class BrowserStepEvidenceError(RuntimeError):
    """Raised when a browser step exits without producing trustworthy evidence."""


def _read_items(path: Path, key: str | None = None) -> list[dict[str, Any]]:
    if not path.is_file():
        return []
    try:
        value: Any = json.loads(path.read_text(encoding="utf-8"))
        if key is not None:
            value = value[key]
    except (OSError, KeyError, TypeError, json.JSONDecodeError) as error:
        raise BrowserStepEvidenceError(f"Invalid browser evidence file: {path.name}") from error
    if not isinstance(value, list) or any(not isinstance(item, dict) for item in value):
        raise BrowserStepEvidenceError(f"Browser evidence is not an object list: {path.name}")
    return value


def snapshot_browser_evidence(output: Path) -> dict[str, Any]:
    """Capture immutable-enough values used to prove evidence was newly appended."""

    output = output.resolve()
    return {
        "operations": _read_items(output / "browser-operations.json"),
        "authChecks": _read_items(output / "browser-results.json", "checks"),
        "fixturesPresent": (output / "fixtures.private.json").is_file(),
        "sampleSizes": {
            name: (output / name).stat().st_size if (output / name).is_file() else None
            for name in ("valid-preview.pdf", "vendor-response.xlsx")
        },
    }


def _successful_delta(
    step: str,
    label: str,
    before: list[dict[str, Any]],
    after: list[dict[str, Any]],
) -> int:
    if after[:len(before)] != before:
        raise BrowserStepEvidenceError(f"{step} replaced or rewrote earlier {label} evidence")
    added = after[len(before):]
    if not added:
        raise BrowserStepEvidenceError(f"{step} produced no new {label} evidence")
    failed = [item for item in added if item.get("status") != "pass"]
    if failed:
        raise BrowserStepEvidenceError(f"{step} produced non-passing {label} evidence")
    return len(added)


def _require_positive_id(value: Any, label: str) -> None:
    if type(value) is not int or value <= 0:
        raise BrowserStepEvidenceError(f"fixtures missing positive id: {label}")


def _validate_fixtures(output: Path) -> dict[str, int]:
    fixture_path = output / "fixtures.private.json"
    try:
        fixtures = json.loads(fixture_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        raise BrowserStepEvidenceError("fixtures did not create valid fixtures.private.json") from error
    if not isinstance(fixtures, dict):
        raise BrowserStepEvidenceError("fixtures.private.json must contain an object")

    roles = fixtures.get("roles")
    if not isinstance(roles, dict):
        raise BrowserStepEvidenceError("fixtures missing roles")
    for role in ("内部成员", "项目管理员"):
        _require_positive_id(roles.get(role), f"roles.{role}")

    suppliers = fixtures.get("suppliers")
    if not isinstance(suppliers, dict):
        raise BrowserStepEvidenceError("fixtures missing suppliers")
    for key in ("a", "b"):
        item = suppliers.get(key)
        if not isinstance(item, dict) or not isinstance(item.get("name"), str) or not item["name"].strip():
            raise BrowserStepEvidenceError(f"fixtures missing supplier: {key}")
        _require_positive_id(item.get("id"), f"suppliers.{key}")

    users = fixtures.get("users")
    if not isinstance(users, dict):
        raise BrowserStepEvidenceError("fixtures missing users")
    for key in ("a", "b", "member", "manager"):
        item = users.get(key)
        if not isinstance(item, dict):
            raise BrowserStepEvidenceError(f"fixtures missing user: {key}")
        _require_positive_id(item.get("id"), f"users.{key}")
        for field in ("username", "password", "changedPassword"):
            if not isinstance(item.get(field), str) or not item[field]:
                raise BrowserStepEvidenceError(f"fixtures missing users.{key}.{field}")

    sample_sizes = {}
    for name in ("valid-preview.pdf", "vendor-response.xlsx"):
        sample = output / name
        if not sample.is_file() or sample.stat().st_size <= 0:
            raise BrowserStepEvidenceError(f"fixtures missing nonempty sample: {name}")
        sample_sizes[name] = sample.stat().st_size
    return sample_sizes


def validate_browser_step_evidence(
    step: str,
    before: dict[str, Any],
    after: dict[str, Any],
    output: Path,
) -> dict[str, Any]:
    """Prove one completed process added the evidence required for its step type."""

    if step == "auth":
        added = _successful_delta(step, "authentication check", before["authChecks"], after["authChecks"])
        return {"step": step, "kind": "authChecks", "added": added}
    if step == "fixtures":
        if before["fixturesPresent"] or not after["fixturesPresent"]:
            raise BrowserStepEvidenceError("fixtures did not newly create its private fixture state")
        sizes = _validate_fixtures(output.resolve())
        return {"step": step, "kind": "fixtures", "added": 1, "sampleSizes": sizes}
    added = _successful_delta(step, "operation", before["operations"], after["operations"])
    return {"step": step, "kind": "operations", "added": added}
