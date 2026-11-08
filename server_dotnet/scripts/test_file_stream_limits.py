"""Real HTTP concurrency contracts for collaboration file streams.

Imported by test-isolated.py after its ordinary file fixture has been uploaded.
The caller owns the disposable database, storage tree, API process and results.
"""

from concurrent.futures import ThreadPoolExecutor
import json
from pathlib import Path
import time
import urllib.error
import urllib.request

from file_blob_fixture import insert_blob_file
from test_native_download_contracts import _new_download_actor


_PER_USER_LIMIT = 4
_LARGE_BYTES = 64 * 1024 * 1024
_INLINE_BYTES = 32 * 1024 * 1024
_SHORT_RANGE = "bytes=0-7"
_EXPECTED_SHORT = bytes(8)


def _cookie(client, name):
    for cookie in client.cookies:
        if cookie.name == name:
            return cookie.value
    raise AssertionError("missing cookie " + name)


def _open(base, path, headers):
    request = urllib.request.Request(
        base + path,
        headers={"Origin": base, **headers},
        method="GET",
    )
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    try:
        return opener.open(request, timeout=30)
    except urllib.error.HTTPError as error:
        return error


def _read(base, path, headers):
    response = _open(base, path, headers)
    try:
        return response.status, response.read(), response.headers
    finally:
        response.close()


def _expect_short_range(base, path, headers, total_size):
    status, payload, response_headers = _read(
        base, path, {**headers, "Range": _SHORT_RANGE})
    if status != 206:
        raise AssertionError(f"GET {path}: expected 206, got {status}")
    if payload != _EXPECTED_SHORT:
        raise AssertionError(f"GET {path}: Range bytes differ")
    if response_headers.get("Content-Range") != f"bytes 0-7/{total_size}":
        raise AssertionError(f"GET {path}: invalid Content-Range")


def _expect_limit(base, path, headers):
    status, payload, _ = _read(base, path, {**headers, "Range": "bytes=0-"})
    if status != 429:
        raise AssertionError(f"GET {path}: expected 429, got {status}")
    try:
        body = json.loads(payload)
    except (TypeError, ValueError) as error:
        raise AssertionError(f"GET {path}: 429 body is not JSON") from error
    if body.get("code") != 42902:
        raise AssertionError(f"GET {path}: expected code 42902, got {body.get('code')}")


def _open_slow(base, path, headers):
    response = _open(base, path, {**headers, "Range": "bytes=0-"})
    if response.status != 206:
        payload = response.read()
        response.close()
        raise AssertionError(f"GET {path}: expected held 206, got {response.status}: {payload[:200]!r}")
    if response.read(1) != b"\0":
        response.close()
        raise AssertionError(f"GET {path}: held response first byte differs")
    return response


def _wait_for_short_range(base, path, headers, total_size, timeout=10.0):
    deadline = time.monotonic() + timeout
    last_status = None
    while time.monotonic() < deadline:
        status, payload, response_headers = _read(
            base, path, {**headers, "Range": _SHORT_RANGE})
        last_status = status
        if status == 206:
            if payload != _EXPECTED_SHORT:
                raise AssertionError(f"GET {path}: released Range bytes differ")
            if response_headers.get("Content-Range") != f"bytes 0-7/{total_size}":
                raise AssertionError(f"GET {path}: released Content-Range differs")
            return
        if status != 429:
            raise AssertionError(f"GET {path}: expected released 206 or transient 429, got {status}")
        time.sleep(0.1)
    raise AssertionError(f"GET {path}: stream lease was not released; last status {last_status}")


def _close_all(responses):
    for response in responses:
        response.close()


def _exercise_path(base, path, headers, total_size, check, label, other=None):
    held = []
    try:
        held = [_open_slow(base, path, headers) for _ in range(_PER_USER_LIMIT)]
        _expect_limit(base, path, headers)
        if other is not None:
            other_path, other_headers = other
            _expect_short_range(base, other_path, other_headers, total_size)
        check(f"{label} rejects the fifth active stream with 42902", True)

        held.pop().close()
        _wait_for_short_range(base, path, headers, total_size)
        check(f"{label} releases one slot after client abort", True)
    finally:
        _close_all(held)

    _wait_for_short_range(base, path, headers, total_size)
    with ThreadPoolExecutor(max_workers=_PER_USER_LIMIT) as pool:
        completed = list(pool.map(
            lambda _: _read(base, path, {**headers, "Range": _SHORT_RANGE}),
            range(_PER_USER_LIMIT),
        ))
    if not all(status == 206 and payload == _EXPECTED_SHORT
               and response_headers.get("Content-Range") == f"bytes 0-7/{total_size}"
               for status, payload, response_headers in completed):
        raise AssertionError(f"{label}: completed Range responses differ")
    _expect_short_range(base, path, headers, total_size)
    check(f"{label} preserves legal Range and releases completed responses", True)


def _native_spec(actor, file_id):
    raw, _ = actor.call("POST", f"/api/v1/files/{file_id}/download-grant", raw=True)
    grant = json.loads(raw)
    handle = grant["url"].rsplit("/", 1)[-1]
    access = actor.token
    actor.token = None
    actor.call("GET", grant["url"], expected=206,
               headers={"Range": "bytes=0-0"}, raw=True)
    session_secret = _cookie(actor, "yf_dls_" + handle)
    actor.token = access
    return grant["url"], {"Cookie": f"yf_dls_{handle}={session_secret}"}


def _media_spec(actor, file_id):
    raw, _ = actor.call("POST", f"/api/v1/files/{file_id}/media-session", raw=True)
    session = json.loads(raw)
    return session["url"], {"Cookie": f"yf_media_{file_id}={_cookie(actor, 'yf_media_' + str(file_id))}"}


def _source_context(conn, source_file_id):
    with conn.cursor() as cursor:
        cursor.execute(
            "SELECT f.project_id,f.uploader_id,us.temp_dir FROM files f "
            "JOIN upload_sessions us ON us.result_file_id=f.id WHERE f.id=%s",
            (source_file_id,),
        )
        row = cursor.fetchone()
    if row is None:
        raise AssertionError("source upload fixture is missing")
    project_id, uploader_id, temp_dir = row
    return project_id, uploader_id, Path(temp_dir).parents[1]


def run_file_stream_limit_checks(admin, conn, check, source_file_id):
    project_id, uploader_id, storage_root = _source_context(conn, source_file_id)
    direct_file = insert_blob_file(
        conn, storage_root, project_id, uploader_id,
        "stream-limit.bin", "bin", "application/octet-stream", size=_LARGE_BYTES)
    inline_file = insert_blob_file(
        conn, storage_root, project_id, uploader_id,
        "stream-limit.pdf", "pdf", "application/pdf", size=_INLINE_BYTES)
    media_file = insert_blob_file(
        conn, storage_root, project_id, uploader_id,
        "stream-limit.webm", "webm", "video/webm", size=_LARGE_BYTES)

    direct_actor, _, _, _ = _new_download_actor(admin, conn)
    other_actor, _, _, _ = _new_download_actor(admin, conn)
    inline_actor, _, _, _ = _new_download_actor(admin, conn)
    native_actor, _, _, _ = _new_download_actor(admin, conn)
    media_actor, _, _, _ = _new_download_actor(admin, conn)

    direct_path = f"/api/v1/files/{direct_file['file_id']}/download"
    direct_headers = {"Authorization": "Bearer " + direct_actor.token}
    other_headers = {"Authorization": "Bearer " + other_actor.token}
    _exercise_path(
        admin.base, direct_path, direct_headers, _LARGE_BYTES, check,
        "direct download",
        other=(direct_path, other_headers),
    )

    inline_path = f"/api/v1/files/{inline_file['file_id']}/content"
    inline_headers = {"Authorization": "Bearer " + inline_actor.token}
    _exercise_path(
        admin.base, inline_path, inline_headers, _INLINE_BYTES, check,
        "inline content",
    )

    native_path, native_headers = _native_spec(native_actor, direct_file["file_id"])
    _exercise_path(
        admin.base, native_path, native_headers, _LARGE_BYTES, check,
        "native Range",
    )

    media_path, media_headers = _media_spec(media_actor, media_file["file_id"])
    _exercise_path(
        admin.base, media_path, media_headers, _LARGE_BYTES, check,
        "media stream",
    )

    shared_actor, _, _, _ = _new_download_actor(admin, conn)
    shared_token_headers = {"Authorization": "Bearer " + shared_actor.token}
    shared_native_path, shared_native_headers = _native_spec(shared_actor, direct_file["file_id"])
    shared_media_path, shared_media_headers = _media_spec(shared_actor, media_file["file_id"])
    shared_specs = [
        (direct_path, shared_token_headers),
        (inline_path, shared_token_headers),
        (shared_native_path, shared_native_headers),
        (shared_media_path, shared_media_headers),
    ]
    held = []
    try:
        held = [_open_slow(admin.base, path, headers) for path, headers in shared_specs]
        _expect_limit(admin.base, direct_path, shared_token_headers)
        _expect_short_range(admin.base, direct_path, other_headers, _LARGE_BYTES)
        check("direct inline native and media share one per-user budget while another user stays isolated", True)
    finally:
        _close_all(held)
    _wait_for_short_range(admin.base, direct_path, shared_token_headers, _LARGE_BYTES)
    check("mixed collaboration streams release their shared budget after abort", True)
