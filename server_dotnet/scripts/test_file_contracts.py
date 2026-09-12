"""Focused file API checks imported by test-isolated.py.

The caller owns the disposable database, storage directory, and API process. This
module only uses those supplied resources and leaves no active upload sessions.
"""

import hashlib
import os
from pathlib import Path
import secrets


def _password():
    return "Files-" + secrets.token_urlsafe(18)


def _abort(client, session_id):
    client.call("DELETE", f"/api/v1/uploads/{session_id}")


def _new_internal_with_permissions(client, conn, codes):
    permissions = client.call("GET", "/api/v1/permissions")
    permission_ids = {item["code"]: item["id"] for item in permissions}
    role = client.call("POST", "/api/v1/admin/roles", {
        "name": "文件预览回归-" + secrets.token_hex(5),
        "description": "owned isolated file preview fixture",
    })
    client.call("PUT", f"/api/v1/admin/roles/{role['id']}/permissions", {
        "permissionIds": [permission_ids[code] for code in codes],
    })
    employee = "filepreview_" + secrets.token_hex(5)
    password = _password()
    user = client.call("POST", "/api/v1/admin/users", {
        "employeeNo": employee,
        "password": password,
        "realName": "文件预览契约用户",
        "email": employee + "@example.invalid",
        "departmentId": None,
        "roleId": role["id"],
    })
    with conn.cursor() as cursor:
        cursor.execute("UPDATE users SET must_change_password=0 WHERE id=%s", (user["id"],))
    actor = type(client)(client.base)
    actor.login(employee, password)
    return actor


def _insert_available_file(conn, project_id, uploader_id, storage_root, extension, content=None, size=None):
    token = secrets.token_hex(8)
    stored_name = token + "." + extension
    path = storage_root / stored_name
    if content is not None:
        path.write_bytes(content)
    else:
        with path.open("wb") as stream:
            stream.truncate(size)
    physical_size = path.stat().st_size
    digest = hashlib.sha256(content).hexdigest() if content is not None else "0" * 64
    mime = {
        "zip": "application/zip",
        "xlsx": "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
    }[extension]
    with conn.cursor() as cursor:
        cursor.execute(
            "INSERT INTO files(project_id,uploader_id,direction,original_name,stored_name,ext,size_bytes,"
            "mime_type,sha256,storage_path,status,deleted_at,created_at) "
            "VALUES(%s,%s,'C2S',%s,%s,%s,%s,%s,%s,%s,'AVAILABLE',NULL,UTC_TIMESTAMP(3))",
            (project_id, uploader_id, stored_name, stored_name, extension, physical_size,
             mime, digest, stored_name),
        )
        return cursor.lastrowid


def run_file_checks(client, conn, check, pid, fid):
    for key in ('targetId', 'page', 'pageSize'):
        for raw in ('', 'abc', '-1', '18446744073709551616', '1&' + key + '=2'):
            rejected = client.call('GET', f'/api/v1/projects/{pid}/files?{key}={raw}', expected=400)
            assert rejected['code'] == 40001 and 'list' not in rejected
    filtered = client.call('GET', f'/api/v1/projects/{pid}/files?targetId={fid}')
    check('file target and pagination reject malformed filters but retain valid targeting',
          filtered['total'] == 1 and filtered['list'][0]['id'] == fid)
    # An uploaded chunk is discovered by a second init with the same strong identity.
    resumed_bytes = b"%PDF-1.4\nfile resume contract\n%%EOF\n"
    resumed_md5 = hashlib.md5(resumed_bytes).hexdigest()
    resumed_name = "resume-" + secrets.token_hex(5) + ".pdf"
    init = client.call("POST", "/api/v1/uploads/init", {
        "projectId": pid, "fileName": resumed_name,
        "fileSize": len(resumed_bytes), "fileMd5": resumed_md5,
    })
    resume_session = init["sessionId"]
    client.call(
        "PUT", f"/api/v1/uploads/{resume_session}/chunks/0", resumed_bytes,
        headers={"Content-Type": "application/octet-stream"},
    )
    resumed = client.call("POST", "/api/v1/uploads/init", {
        "projectId": pid, "fileName": resumed_name,
        "fileSize": len(resumed_bytes), "fileMd5": resumed_md5,
    })
    check(
        "file init resumes matching upload and reports chunks",
        resumed["sessionId"] == resume_session
        and resumed.get("resumed") is True
        and resumed["uploadedChunks"] == [0],
    )
    merged = client.call("POST", f"/api/v1/uploads/{resume_session}/merge")
    disposable_fid = merged.get("id", merged.get("fileId"))
    check("file resumed upload merges", disposable_fid is not None)

    # A valid extension is required before any session or directory is created.
    rejected = client.call("POST", "/api/v1/uploads/init", {
        "projectId": pid, "fileName": "blocked-" + secrets.token_hex(4) + ".exe",
        "fileSize": 4, "fileMd5": hashlib.md5(b"nope").hexdigest(),
    }, expected=400)
    check("file init rejects non-whitelisted extension", "文件类型" in rejected["message"])

    # A complete byte stream with the wrong declared MD5 must not produce a file.
    bad_md5_bytes = b"%PDF-1.4\nwrong digest contract\n%%EOF\n"
    bad = client.call("POST", "/api/v1/uploads/init", {
        "projectId": pid, "fileName": "bad-md5-" + secrets.token_hex(5) + ".pdf",
        "fileSize": len(bad_md5_bytes), "fileMd5": "0" * 32,
    })
    bad_session = bad["sessionId"]
    try:
        client.call(
            "PUT", f"/api/v1/uploads/{bad_session}/chunks/0", bad_md5_bytes,
            headers={"Content-Type": "application/octet-stream"},
        )
        mismatch = client.call("POST", f"/api/v1/uploads/{bad_session}/merge", expected=400)
        state = client.call("GET", f"/api/v1/uploads/{bad_session}")
        check(
            "file merge rejects bad MD5 and restores upload state",
            "MD5" in mismatch["message"] and state["status"] == "UPLOADING",
        )
    finally:
        _abort(client, bad_session)

    # Force two chunks and omit the tail. Merge must reject before taking its lease.
    missing_bytes = b"M" * 262145
    missing = client.call("POST", "/api/v1/uploads/init", {
        "projectId": pid, "fileName": "missing-" + secrets.token_hex(5) + ".pdf",
        "fileSize": len(missing_bytes), "fileMd5": hashlib.md5(missing_bytes).hexdigest(),
    })
    missing_session = missing["sessionId"]
    try:
        check("file missing-chunk fixture uses multiple chunks", missing["totalChunks"] > 1)
        first_chunk = missing_bytes[:missing["chunkSize"]]
        client.call(
            "PUT", f"/api/v1/uploads/{missing_session}/chunks/0", first_chunk,
            headers={"Content-Type": "application/octet-stream"},
        )
        incomplete = client.call("POST", f"/api/v1/uploads/{missing_session}/merge", expected=400)
        check("file merge rejects missing chunks", "分片不完整" in incomplete["message"])
    finally:
        _abort(client, missing_session)

    # Abort is idempotent, removes the session directory, and writes one audit event.
    abort_bytes = b"abort contract"
    abort_init = client.call("POST", "/api/v1/uploads/init", {
        "projectId": pid, "fileName": "abort-" + secrets.token_hex(5) + ".pdf",
        "fileSize": len(abort_bytes), "fileMd5": hashlib.md5(abort_bytes).hexdigest(),
    })
    abort_session = abort_init["sessionId"]
    with conn.cursor() as cursor:
        cursor.execute("SELECT temp_dir FROM upload_sessions WHERE id=%s", (abort_session,))
        abort_dir = Path(cursor.fetchone()[0])
    client.call(
        "PUT", f"/api/v1/uploads/{abort_session}/chunks/0", abort_bytes,
        headers={"Content-Type": "application/octet-stream"},
    )
    _abort(client, abort_session)
    _abort(client, abort_session)
    abort_state = client.call("GET", f"/api/v1/uploads/{abort_session}")
    with conn.cursor() as cursor:
        cursor.execute(
            "SELECT COUNT(*) FROM audit_logs "
            "WHERE action='UPLOAD_ABORT' AND target_type='upload_session' AND target_id=%s",
            (abort_session,),
        )
        abort_audits = cursor.fetchone()[0]
    check(
        "file abort is idempotent audited and cleans chunks",
        abort_state["status"] == "ABORTED" and abort_audits == 1 and not abort_dir.exists(),
    )

    # Unsatisfiable ranges must use the standard HTTP 416 contract.
    client.call(
        "GET", f"/api/v1/files/{fid}/content", expected=416,
        headers={"Range": "bytes=999999999-1000000000"}, raw=True,
    )
    check("file preview rejects unsatisfiable range", True)

    # The supplier can see this project but its fixed role has no destructive permission.
    with conn.cursor() as cursor:
        cursor.execute("SELECT supplier_id FROM projects WHERE id=%s", (pid,))
        supplier_id = cursor.fetchone()[0]
    employee = "fileperm_" + secrets.token_hex(5)
    password = _password()
    account = client.call("POST", f"/api/v1/admin/suppliers/{supplier_id}/accounts", {
        "employeeNo": employee, "password": password, "realName": "文件权限契约用户",
        "email": employee + "@example.invalid",
    })
    with conn.cursor() as cursor:
        cursor.execute("UPDATE users SET must_change_password=0 WHERE id=%s", (account["id"],))
    supplier_client = type(client)(client.base)
    supplier_client.login(employee, password)
    listing = supplier_client.call("GET", f"/api/v1/projects/{pid}/files?targetId={disposable_fid}")
    supplier_client.call("DELETE", f"/api/v1/files/{disposable_fid}", expected=403)
    with conn.cursor() as cursor:
        cursor.execute("SELECT status FROM files WHERE id=%s", (disposable_fid,))
        status_after_denial = cursor.fetchone()[0]
    check(
        "file delete permission is reflected and enforced",
        len(listing["list"]) == 1
        and listing["list"][0]["canDelete"] is False
        and status_after_denial == "AVAILABLE",
    )
    client.call("DELETE", f"/api/v1/files/{disposable_fid}")
    with conn.cursor() as cursor:
        cursor.execute("SELECT status,deleted_at FROM files WHERE id=%s", (disposable_fid,))
        deleted_status, deleted_at = cursor.fetchone()
    check("file delete soft-deletes for authorized actor", deleted_status == "DELETED" and deleted_at is not None)

    # Even an authorized request must never follow a database path outside StorageRoot.
    with conn.cursor() as cursor:
        cursor.execute(
            "SELECT f.storage_path,f.uploader_id,us.temp_dir FROM files f "
            "JOIN upload_sessions us ON us.result_file_id=f.id WHERE f.id=%s",
            (fid,),
        )
        original_storage_path, uploader_id, completed_temp_dir = cursor.fetchone()
    storage_root = Path(completed_temp_dir).parents[1]

    # Preview and download are independent permissions. Inline content is only
    # available for the three browser-supported formats and has a common 50 MiB cap.
    preview_client = _new_internal_with_permissions(
        client, conn, ["project:list", "project:view_all", "file:preview"])
    preview_pdf, _ = preview_client.call("GET", f"/api/v1/files/{fid}/content", raw=True)
    preview_client.call("GET", f"/api/v1/files/{fid}/download", expected=403)
    zip_bytes = b"PK\x03\x04file preview boundary"
    zip_fid = _insert_available_file(
        conn, pid, uploader_id, storage_root, "zip", content=zip_bytes)
    oversized_xlsx_fid = _insert_available_file(
        conn, pid, uploader_id, storage_root, "xlsx", size=50 * 1024 * 1024 + 1)
    preview_client.call("GET", f"/api/v1/files/{zip_fid}/content", expected=400)
    preview_client.call("GET", f"/api/v1/files/{oversized_xlsx_fid}/content", expected=400)
    downloaded_zip, _ = client.call("GET", f"/api/v1/files/{zip_fid}/download", raw=True)
    check(
        "file preview permission enforces type size and independent download boundaries",
        preview_pdf.startswith(b"%PDF") and downloaded_zip == zip_bytes,
    )

    outside_dir = storage_root.parent / ("file-contract-outside-" + secrets.token_hex(5))
    outside_file = outside_dir / "secret.pdf"
    outside_dir.mkdir()
    outside_file.write_bytes(b"must never be served")
    escape_path = os.path.relpath(outside_file, storage_root)
    try:
        with conn.cursor() as cursor:
            cursor.execute("UPDATE files SET storage_path=%s WHERE id=%s", (escape_path, fid))
        escaped = client.call("GET", f"/api/v1/files/{fid}/download", expected=500)
        check(
            "file download rejects storage path outside root",
            escaped["code"] == 50000 and outside_file.read_bytes() == b"must never be served",
        )
    finally:
        with conn.cursor() as cursor:
            cursor.execute("UPDATE files SET storage_path=%s WHERE id=%s", (original_storage_path, fid))
        outside_file.unlink(missing_ok=True)
        outside_dir.rmdir()
