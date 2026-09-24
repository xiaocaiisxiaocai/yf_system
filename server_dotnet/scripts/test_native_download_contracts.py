"""Real HTTP contracts for cookie-scoped native file downloads.

Imported by test-isolated.py after its ordinary file fixture has been uploaded.
The caller owns the disposable database, storage tree, API process and result collector.
"""

from concurrent.futures import ThreadPoolExecutor
import io
import json
from pathlib import Path
import re
import secrets
import time
import urllib.error
import urllib.request
import zipfile

from file_blob_fixture import insert_blob_file


_HANDLE = re.compile(r"^[0-9a-f]{32}$")


def _password():
    return "Yf9!" + secrets.token_urlsafe(9)


def _new_download_actor(admin, conn):
    permissions = admin.call("GET", "/api/v1/permissions")
    permission_ids = {item["code"]: item["id"] for item in permissions}
    role = admin.call("POST", "/api/v1/admin/roles", {
        "name": "原生下载回归-" + secrets.token_hex(5),
        "description": "owned isolated native download fixture",
    })
    admin.call("PUT", f"/api/v1/admin/roles/{role['id']}/permissions", {
        "permissionIds": [permission_ids[code] for code in (
            "project:list", "project:view_all", "file:download", "file:preview")],
    })
    department = admin.call("POST", "/api/v1/admin/departments", {
        "name": "原生下载测试组织-" + secrets.token_hex(5),
        "parentId": None,
        "sortNo": 0,
    })
    employee = "native_download_" + secrets.token_hex(5)
    password = _password()
    user = admin.call("POST", "/api/v1/admin/users", {
        "employeeNo": employee,
        "password": password,
        "realName": "原生下载契约用户",
        "email": employee + "@example.invalid",
        "departmentId": department["id"],
        "roleId": role["id"],
    })
    with conn.cursor() as cursor:
        cursor.execute("UPDATE users SET must_change_password=0 WHERE id=%s", (user["id"],))
    actor = type(admin)(admin.base)
    actor.login(employee, password)
    return actor, user, employee, password


def _cookie(client, name):
    for cookie in client.cookies:
        if cookie.name == name:
            return cookie.value
    raise AssertionError("missing cookie " + name)


def _set_cookie_values(headers):
    return headers.get_all("Set-Cookie") or []


def _direct_call(base, method, path, expected, headers=None, body=None):
    request = urllib.request.Request(
        base + path,
        data=body,
        headers={"Origin": base, **(headers or {})},
        method=method,
    )
    try:
        response = urllib.request.urlopen(request, timeout=30)
    except urllib.error.HTTPError as error:
        response = error
    payload = response.read()
    if response.status != expected:
        raise AssertionError(f"{method} {path}: expected {expected}, got {response.status}")
    return payload, response.headers


def _client_raw(client, method, path, headers=None, read_all=True):
    request = urllib.request.Request(
        client.base + path,
        headers={"Origin": client.base, **(headers or {})},
        method=method,
    )
    try:
        response = client.opener.open(request, timeout=30)
    except urllib.error.HTTPError as error:
        response = error
    payload = response.read() if read_all else response
    return response.status, payload, response.headers


def _issue_single(actor, file_id):
    raw, headers = actor.call("POST", f"/api/v1/files/{file_id}/download-grant", raw=True)
    grant = json.loads(raw)
    handle = grant["url"].rsplit("/", 1)[-1]
    if not _HANDLE.fullmatch(handle):
        raise AssertionError("download URL does not contain an exact public handle")
    return grant, handle, headers, _cookie(actor, "yf_dlg_" + handle)


def _issue_batch(actor, file_ids):
    raw, headers = actor.call(
        "POST", "/api/v1/files/batch-download-grant", {"ids": file_ids}, raw=True)
    grant = json.loads(raw)
    handle = grant["url"].rsplit("/", 1)[-1]
    if not _HANDLE.fullmatch(handle):
        raise AssertionError("batch URL does not contain an exact public handle")
    return grant, handle, headers, _cookie(actor, "yf_dlg_" + handle)


def _audit_count(conn, user_id, action, file_id=None):
    with conn.cursor() as cursor:
        if file_id is None:
            cursor.execute(
                "SELECT COUNT(*) FROM audit_logs WHERE user_id=%s AND action=%s",
                (user_id, action),
            )
        else:
            cursor.execute(
                "SELECT COUNT(*) FROM audit_logs WHERE user_id=%s AND action=%s "
                "AND target_type='file' AND target_id=%s",
                (user_id, action, str(file_id)),
            )
        return cursor.fetchone()[0]


def _insert_large_zip(conn, source_file_id):
    with conn.cursor() as cursor:
        cursor.execute(
            "SELECT f.project_id,f.uploader_id,us.temp_dir FROM files f "
            "JOIN upload_sessions us ON us.result_file_id=f.id WHERE f.id=%s",
            (source_file_id,),
        )
        project_id, uploader_id, temp_dir = cursor.fetchone()
    storage_root = Path(temp_dir).parents[1]
    return insert_blob_file(
        conn, storage_root, project_id, uploader_id,
        "native-abort-large.zip", "zip", "application/zip", size=64 * 1024 * 1024,
    )["file_id"]


def run_native_download_checks(admin, conn, check, file_id, expected_bytes):
    actor, user, employee, password = _new_download_actor(admin, conn)
    user_id = user["id"]

    grant, handle, grant_headers, grant_secret = _issue_single(actor, file_id)
    grant_cookies = "\n".join(_set_cookie_values(grant_headers)).lower()
    check(
        "native download grant uses a short HttpOnly path-scoped cookie and a secret-free URL",
        grant["expiresInSeconds"] == 60
        and "?" not in grant["url"]
        and actor.token not in grant["url"]
        and "httponly" in grant_cookies
        and "samesite=strict" in grant_cookies
        and "max-age=60" in grant_cookies
        and f"path={grant['url']}" in grant_cookies,
    )

    before_download_audit = _audit_count(conn, user_id, "FILE_DOWNLOAD", file_id)
    access = actor.token
    actor.token = None
    first, first_headers = actor.call(
        "GET", grant["url"], expected=206, headers={"Range": "bytes=0-7"}, raw=True)
    session_secret = _cookie(actor, "yf_dls_" + handle)
    session_cookies = "\n".join(_set_cookie_values(first_headers)).lower()
    check(
        "native download redeems to a resumable short session without bearer",
        first == expected_bytes[:8]
        and first_headers.get("Content-Range") == f"bytes 0-7/{len(expected_bytes)}"
        and "httponly" in session_cookies
        and "samesite=strict" in session_cookies
        and "max-age=900" in session_cookies
        and f"path={grant['url']}" in session_cookies,
    )

    native_csp = first_headers.get("Content-Security-Policy", "")
    grant_csp = grant_headers.get("Content-Security-Policy", "")
    check(
        "only native download responses may be framed, and only by the same origin",
        first_headers.get("X-Frame-Options") == "SAMEORIGIN"
        and native_csp.startswith("frame-ancestors 'self';")
        and "default-src 'none'" in native_csp
        and "sandbox allow-same-origin" in native_csp
        and grant_headers.get("X-Frame-Options") == "DENY"
        and grant_csp == "frame-ancestors 'none'",
    )

    cookie_header = {"Cookie": f"yf_dls_{handle}={session_secret}"}
    offsets = list(range(8, 18))
    with ThreadPoolExecutor(max_workers=10) as pool:
        results = list(pool.map(
            lambda offset: _direct_call(
                actor.base, "GET", grant["url"], 206,
                {**cookie_header, "Range": f"bytes={offset}-{offset}"}),
            offsets,
        ))
    check(
        "ten concurrent native ranges share the fixed session and exact bytes",
        all(payload == expected_bytes[offset:offset + 1]
            and headers.get("Content-Range") == f"bytes {offset}-{offset}/{len(expected_bytes)}"
            for offset, (payload, headers) in zip(offsets, results)),
    )

    resumed, _ = _direct_call(
        actor.base, "GET", grant["url"], 206,
        {**cookie_header, "Range": "bytes=18-25"})
    _direct_call(
        actor.base, "GET", grant["url"], 401,
        {"Cookie": f"yf_dlg_{handle}={grant_secret}", "Range": "bytes=0-0"})
    wrong_id = file_id + 1 if file_id < 9223372036854775807 else file_id - 1
    wrong_url = grant["url"].replace(f"/files/{file_id}/", f"/files/{wrong_id}/")
    _direct_call(actor.base, "GET", wrong_url, 401, cookie_header)
    with conn.cursor() as cursor:
        cursor.execute(
            "SELECT detail FROM audit_logs WHERE user_id=%s AND action='FILE_DOWNLOAD' "
            "AND target_type='file' AND target_id=%s ORDER BY id",
            (user_id, str(file_id)),
        )
        download_details = [row[0] for row in cursor.fetchall()]
    check(
        "native grant is single-use file-scoped and Range audit is exactly once without secrets",
        resumed == expected_bytes[18:26]
        and _audit_count(conn, user_id, "FILE_DOWNLOAD", file_id) - before_download_audit == 1
        and len(download_details) == 1
        and all(secret not in download_details[0] for secret in (handle, grant_secret, session_secret)),
    )

    actor.token = access
    before_preview_audit = _audit_count(conn, user_id, "FILE_PREVIEW", file_id)
    actor.call("GET", f"/api/v1/files/{file_id}/content", expected=206,
               headers={"Range": "bytes=0-3"}, raw=True)
    actor.call("GET", f"/api/v1/files/{file_id}/content", expected=206,
               headers={"Range": "bytes=4-7"}, raw=True)
    check(
        "preview Range requests are audited once within the bounded window",
        _audit_count(conn, user_id, "FILE_PREVIEW", file_id) - before_preview_audit == 1,
    )

    batch, batch_handle, batch_headers, _ = _issue_batch(actor, [file_id])
    batch_cookie = "\n".join(_set_cookie_values(batch_headers)).lower()
    actor.token = None
    archive, _ = actor.call("GET", batch["url"], raw=True)
    with zipfile.ZipFile(io.BytesIO(archive)) as zipped:
        names = zipped.namelist()
        valid_archive = zipped.testzip() is None and len(names) == 1 and zipped.read(names[0]) == expected_bytes
    archive_again, _ = actor.call("GET", batch["url"], raw=True)
    with zipfile.ZipFile(io.BytesIO(archive_again)) as zipped:
        valid_reuse = zipped.testzip() is None and zipped.read(zipped.namelist()[0]) == expected_bytes
    check(
        "native batch grant streams a reusable-session ZIP and releases its concurrency lease",
        valid_archive and valid_reuse
        and "httponly" in batch_cookie and "max-age=60" in batch_cookie
        and _audit_count(conn, user_id, "FILE_BATCH_DOWNLOAD") == 1,
    )

    # Closing a large ZIP response after the first byte must release the per-user limiter.
    large_file_id = _insert_large_zip(conn, file_id)
    actor.token = access
    aborted, _, _, _ = _issue_batch(actor, [large_file_id])
    actor.token = None
    status, response, _ = _client_raw(actor, "GET", aborted["url"], read_all=False)
    if status != 200:
        response.close()
        raise AssertionError(f"large native batch expected 200, got {status}")
    response.read(1)
    response.close()
    actor.token = access
    following, _, _, _ = _issue_batch(actor, [file_id])
    actor.token = None
    released = False
    for _ in range(50):
        status, payload, _ = _client_raw(actor, "GET", following["url"])
        if status == 200:
            with zipfile.ZipFile(io.BytesIO(payload)) as zipped:
                released = zipped.testzip() is None and zipped.read(zipped.namelist()[0]) == expected_bytes
            break
        if status != 409:
            raise AssertionError(f"post-abort native batch expected 200/409, got {status}")
        time.sleep(0.1)
    check("aborted ZIP stream releases the batch concurrency lease", released)

    # A session belongs to the login family that issued it. Another active login does not keep it alive.
    cross_actor, cross_user, cross_employee, cross_password = _new_download_actor(admin, conn)
    second_login = type(admin)(admin.base)
    second_login.login(cross_employee, cross_password)
    cross_grant, cross_handle, _, _ = _issue_single(cross_actor, file_id)
    cross_access = cross_actor.token
    cross_actor.token = None
    cross_actor.call("GET", cross_grant["url"], expected=206,
                     headers={"Range": "bytes=0-0"}, raw=True)
    cross_secret = _cookie(cross_actor, "yf_dls_" + cross_handle)
    cross_actor.token = cross_access
    cross_actor.call("POST", "/api/v1/auth/logout")
    _direct_call(
        cross_actor.base, "GET", cross_grant["url"], 401,
        {"Cookie": f"yf_dls_{cross_handle}={cross_secret}", "Range": "bytes=0-0"})
    second_login.call("GET", "/api/v1/auth/profile")
    check("download session stops with its issuing login while another login remains active", True)

    disabled_actor, disabled_user, _, _ = _new_download_actor(admin, conn)
    disabled_grant, disabled_handle, _, _ = _issue_single(disabled_actor, file_id)
    disabled_actor.token = None
    disabled_actor.call("GET", disabled_grant["url"], expected=206,
                        headers={"Range": "bytes=0-0"}, raw=True)
    disabled_secret = _cookie(disabled_actor, "yf_dls_" + disabled_handle)
    admin.call("PUT", f"/api/v1/admin/users/{disabled_user['id']}/status", {"status": "DISABLED"})
    _direct_call(
        disabled_actor.base, "GET", disabled_grant["url"], 401,
        {"Cookie": f"yf_dls_{disabled_handle}={disabled_secret}", "Range": "bytes=1-1"})
    check("disabling the user blocks the next native Range request", True)
