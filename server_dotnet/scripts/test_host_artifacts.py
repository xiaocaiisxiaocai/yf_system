"""Verify that TestHost will load the intended API build and dependencies."""

from __future__ import annotations

import hashlib
from pathlib import Path


class TestHostArtifactError(RuntimeError):
    """Raised when TestHost would load a missing or different runtime artifact."""


def _digest(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def verify_test_host_artifacts(api_directory: Path, test_host_dll: Path, *, use_api_runtime: bool = False) -> dict[Path, str]:
    """Return hashes of the actual TestHost payload after proving build identity.

    A framework-dependent .NET executable resolves project-reference assemblies and
    shared managed dependencies beside its entry DLL.  Comparing only the API output
    used for database initialization does not therefore prove which API build the
    TestHost process will execute.
    """

    api_directory = api_directory.resolve()
    test_host_dll = test_host_dll.resolve()
    host_directory = test_host_dll.parent
    api_dll = api_directory / "Yf.Api.dll"
    if not api_dll.is_file():
        raise TestHostArtifactError(f"Intended API assembly is missing: {api_dll}")
    if not test_host_dll.is_file():
        raise TestHostArtifactError(f"TestHost entry assembly is missing: {test_host_dll}")

    # All managed DLLs copied to the API output are part of the resolved application
    # payload.  Requiring byte identity is deliberately stricter than comparing file
    # versions, which can remain unchanged across local rebuilds.
    intended_runtime = sorted(
        path for path in api_directory.iterdir()
        if path.is_file() and path.suffix.lower() == ".dll"
    )
    actual_hashes: dict[Path, str] = {}
    for intended in intended_runtime:
        actual = host_directory / intended.name
        if not actual.is_file():
            raise TestHostArtifactError(
                f"TestHost runtime dependency is missing: {actual.name}"
            )
        intended_hash = _digest(intended)
        actual_hash = _digest(actual)
        if actual_hash != intended_hash:
            raise TestHostArtifactError(
                f"TestHost runtime dependency differs from the intended API build: {actual.name}"
            )
        actual_hashes[actual] = actual_hash

    # Published verification explicitly launches dotnet exec with the production
    # deps/runtimeconfig, rather than reintroducing development-only dependencies.
    resolution_name = "Yf.Api" if use_api_runtime else test_host_dll.stem
    for name in (
        test_host_dll.name,
        resolution_name + ".deps.json",
        resolution_name + ".runtimeconfig.json",
    ):
        artifact = host_directory / name
        if not artifact.is_file():
            raise TestHostArtifactError(f"TestHost launch artifact is missing: {name}")
        if use_api_runtime and name != test_host_dll.name:
            intended = api_directory / name
            if not intended.is_file() or _digest(intended) != _digest(artifact):
                raise TestHostArtifactError(f"TestHost production resolution metadata differs: {name}")
        actual_hashes[artifact] = _digest(artifact)

    return actual_hashes
