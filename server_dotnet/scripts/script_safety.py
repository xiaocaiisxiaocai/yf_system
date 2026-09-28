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


# Characters Windows rejects in a path segment (plus ASCII control characters).
_WINDOWS_ILLEGAL_CHARACTERS = frozenset('<>:"|?*') | frozenset(chr(code) for code in range(32))
# Device names are reserved with or without an extension ("NUL", "nul.txt", "COM1.log").
_WINDOWS_RESERVED_NAMES = frozenset(
    {"CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$"}
    | {f"COM{index}" for index in range(1, 10)}
    | {f"LPT{index}" for index in range(1, 10)}
    | {"COM\u00b9", "COM\u00b2", "COM\u00b3", "LPT\u00b9", "LPT\u00b2", "LPT\u00b3"}
)
# Release packages are well below these; the limits bound a hostile or corrupt archive
# (zip bomb / entry flood) before anything is extracted.
MAX_ARCHIVE_TOTAL_BYTES = 2 * 1024 * 1024 * 1024
MAX_ARCHIVE_ENTRIES = 50_000


def _is_unsafe_windows_segment(segment: str) -> bool:
    if not segment or segment in {".", ".."}:
        return True
    if segment[-1] in {".", " "}:
        return True
    if any(character in _WINDOWS_ILLEGAL_CHARACTERS for character in segment):
        return True
    return segment.split(".", 1)[0].rstrip(" ").upper() in _WINDOWS_RESERVED_NAMES


def validate_zip_entries(
    entries: Iterable[zipfile.ZipInfo],
    *,
    max_total_bytes: int = MAX_ARCHIVE_TOTAL_BYTES,
    max_entries: int = MAX_ARCHIVE_ENTRIES,
) -> None:
    """Reject paths that are unsafe or ambiguous on the Windows release target.

    Also bounds the declared uncompressed size and the number of entries so a
    verification run cannot be used to fill the disk.
    """

    targets: set[str] = set()
    total_bytes = 0
    count = 0
    for entry in entries:
        count += 1
        if count > max_entries:
            raise RuntimeError("Archive has too many entries")
        total_bytes += max(entry.file_size, 0)
        if total_bytes > max_total_bytes:
            raise RuntimeError("Archive uncompressed size limit exceeded")
        path = PurePosixPath(entry.filename)
        segments = entry.filename[:-1].split("/") if entry.filename.endswith("/") else entry.filename.split("/")
        if (
            path.is_absolute()
            or ".." in path.parts
            or "\\" in entry.filename
            or ":" in entry.filename
            or (entry.external_attr >> 16) & 0o170000 == 0o120000
            or any(_is_unsafe_windows_segment(segment) for segment in segments)
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
