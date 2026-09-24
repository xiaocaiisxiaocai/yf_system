"""Shared safety helpers for local validation entry points."""

from __future__ import annotations

from collections.abc import Iterable, Mapping
import hashlib
import json
import os
from pathlib import Path
from pathlib import PurePosixPath
import zipfile


def clean_dotnet_config_environment(source: Mapping[str, str] | None = None) -> dict[str, str]:
    """Copy an environment without inherited application configuration or bootstrap secrets."""

    environment = os.environ if source is None else source
    return {
        key: value
        for key, value in environment.items()
        if not key.lower().startswith(("app__", "app:"))
        and key.upper() not in {"YF_CONFIG_PATH", "YF_BOOTSTRAP_PASSWORD"}
    }


def validate_zip_entries(entries: Iterable[zipfile.ZipInfo]) -> None:
    """Reject paths that are unsafe or ambiguous on the Windows release target."""

    targets: set[str] = set()
    for entry in entries:
        path = PurePosixPath(entry.filename)
        if (
            path.is_absolute()
            or ".." in path.parts
            or "\\" in entry.filename
            or ":" in entry.filename
            or (entry.external_attr >> 16) & 0o170000 == 0o120000
        ):
            raise RuntimeError("Unsafe archive path")
        target = path.as_posix().rstrip("/").casefold()
        if not target or target in targets:
            raise RuntimeError("Duplicate archive path")
        targets.add(target)


def _digest(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def validate_release_sidecars(
    archive: Path,
    package_manifest: Mapping[str, object],
    actual_files: Mapping[str, Mapping[str, object]],
) -> dict[str, object]:
    """Validate the sidecars emitted beside a release ZIP by publish-iis.ps1."""

    hash_path = Path(str(archive) + ".sha256")
    release_path = archive.with_suffix(".release-manifest.json")
    if not hash_path.is_file() or not release_path.is_file():
        raise RuntimeError("Release sidecar is missing")

    archive_hash = _digest(archive)
    hash_parts = hash_path.read_text(encoding="utf-8-sig").strip().split(maxsplit=1)
    if hash_parts != [archive_hash, archive.name]:
        raise RuntimeError("Release hash sidecar mismatch")
    try:
        release = json.loads(release_path.read_text(encoding="utf-8-sig"))
        archive_record = release["archive"]
        release_files = release["files"]
    except (OSError, KeyError, TypeError, json.JSONDecodeError) as error:
        raise RuntimeError("Release manifest is invalid") from error
    if not isinstance(release, dict) or not isinstance(archive_record, dict):
        raise RuntimeError("Release manifest is invalid")
    if (
        archive_record.get("path") != archive.name
        or archive_record.get("sha256") != archive_hash
        or archive_record.get("bytes") != archive.stat().st_size
    ):
        raise RuntimeError("Release archive metadata mismatch")
    if (release.get("source") != package_manifest.get("source")
            or release.get("build") != package_manifest.get("build")
            or release.get("configuration") != package_manifest.get("configuration")):
        raise RuntimeError("Inner and outer release provenance mismatch")

    normalized: dict[str, dict[str, object]] = {}
    if not isinstance(release_files, list):
        raise RuntimeError("Release file manifest is invalid")
    for item in release_files:
        if not isinstance(item, dict) or not isinstance(item.get("path"), str) or item["path"] in normalized:
            raise RuntimeError("Release file manifest is invalid")
        normalized[item["path"]] = {
            "sha256": item.get("sha256"),
            "bytes": item.get("bytes"),
        }
    if normalized != {name: dict(metadata) for name, metadata in actual_files.items()}:
        raise RuntimeError("Release file manifest mismatch")
    return release
