"""Focused file API checks imported by test-isolated.py.

The caller owns the disposable database, storage directory, and API process. This
module only uses those supplied resources and leaves no active upload sessions.
"""

import hashlib
import json
import os
from pathlib import Path
import secrets

from file_blob_fixture import insert_blob_file
from test_business_acceptance import _create_project_group
from upload_contract import (
    FINGERPRINT_SAMPLE_SIZE,
    file_fingerprint,
    init_request,
    init_upload,
    put_chunk,
    submit_md5,
    upload_bytes,
)


def _password():
    return "Yf9!" + secrets.token_urlsafe(9)


def _abort(client, session_id):
    client.call("DELETE", f"/api/v1/uploads/{session_id}")


def _upload_file(client, project_id, file_name, content):
    _, _, merged = upload_bytes(client, project_id, file_name, content)
    return merged["id"]


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
    department = client.call("POST", "/api/v1/admin/departments", {
        "name": "文件预览测试组织-" + secrets.token_hex(5),
        "parentId": None,
        "sortNo": 0,
    })
    user = client.call("POST", "/api/v1/admin/users", {
        "employeeNo": employee,
        "password": password,
        "realName": "文件预览契约用户",
        "email": employee + "@example.invalid",
        "departmentId": department["id"],
        "roleId": role["id"],
    })
    with conn.cursor() as cursor:
        cursor.execute("UPDATE users SET must_change_password=0 WHERE id=%s", (user["id"],))
    actor = type(client)(client.base)
    actor.login(employee, password)
    return actor


def _insert_available_file(conn, project_id, uploader_id, storage_root, extension, content=None, size=None):
    mime = {
        "zip": "application/zip",
        "xlsx": "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "pptx": "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        "mp4": "video/mp4",
    }[extension]
    original_name = secrets.token_hex(8) + "." + extension
    return insert_blob_file(
        conn, storage_root, project_id, uploader_id, original_name, extension, mime,
        content=content, size=size,
    )["file_id"]


def run_file_checks(client, conn, check, pid, fid):
    configs = client.call("GET", "/api/v1/admin/system/configs")
    allowed_extensions = next(item["value"] for item in configs if item["key"] == "upload.allowed_exts").split(",")
    check(
        "new database image allowlist extends the existing upload defaults",
        set(("gif", "webp", "bmp")).issubset(allowed_extensions)
        and set(("pdf", "docx", "xlsx", "pptx", "png", "jpg", "jpeg", "zip", "mp4", "webm", "ogv")).issubset(allowed_extensions),
    )

    for key in ('targetId', 'page', 'pageSize'):
        for raw in ('', 'abc', '-1', '18446744073709551616', '1&' + key + '=2'):
            rejected = client.call('GET', f'/api/v1/projects/{pid}/files?{key}={raw}', expected=400)
            assert rejected['code'] == 40001 and 'list' not in rejected
    filtered = client.call('GET', f'/api/v1/projects/{pid}/files?targetId={fid}')
    check('file target and pagination reject malformed filters but retain valid targeting',
          filtered['total'] == 1 and filtered['list'][0]['id'] == fid)
    # The quick fingerprint intentionally samples only the first and last MiB.
    # Per-chunk digests let a resumed client detect and replace a changed middle.
    prefix = b"P" * FINGERPRINT_SAMPLE_SIZE
    suffix = b"S" * FINGERPRINT_SAMPLE_SIZE
    resumed_bytes = prefix + b"A" * 262144 + suffix
    changed_bytes = prefix + b"B" * 262144 + suffix
    resumed_name = "resume-" + secrets.token_hex(5) + ".pdf"
    check(
        "file quick fingerprint permits same-metadata middle changes",
        resumed_bytes != changed_bytes
        and file_fingerprint(resumed_name, resumed_bytes)
        == file_fingerprint(resumed_name, changed_bytes),
    )
    init = init_upload(client, pid, resumed_name, resumed_bytes)
    resume_session = init["sessionId"]
    middle_index = FINGERPRINT_SAMPLE_SIZE // init["chunkSize"]
    middle_start = middle_index * init["chunkSize"]
    original_middle = resumed_bytes[middle_start:middle_start + init["chunkSize"]]
    changed_middle = changed_bytes[middle_start:middle_start + init["chunkSize"]]
    put_chunk(client, resume_session, middle_index, original_middle)
    resumed = init_upload(client, pid, resumed_name, changed_bytes)
    check(
        "file init resumes matching quick identity and reports chunk digests",
        resumed["sessionId"] == resume_session
        and resumed.get("resumed") is True
        and resumed["uploadedChunks"] == [{
            "index": middle_index,
            "sha256": hashlib.sha256(original_middle).hexdigest(),
        }]
        and resumed["uploadedChunks"][0]["sha256"] != hashlib.sha256(changed_middle).hexdigest(),
    )
    mismatch = put_chunk(
        client, resume_session, 0, changed_bytes[:init["chunkSize"]],
        expected=400, declared_sha256="0" * 64,
    )
    check("file chunk upload rejects a declared SHA-256 that differs from the body",
          "SHA-256" in mismatch["message"])
    for index in range(init["totalChunks"]):
        start = index * init["chunkSize"]
        put_chunk(client, resume_session, index, changed_bytes[start:start + init["chunkSize"]])
    submit_md5(client, resume_session, changed_bytes)
    merged = client.call("POST", f"/api/v1/uploads/{resume_session}/merge")
    disposable_fid = merged["id"]
    resumed_download, _ = client.call(
        "GET", f"/api/v1/files/{disposable_fid}/download", raw=True)
    check("file resumed upload replaces a changed middle and merges exact bytes",
          resumed_download == changed_bytes)

    # A valid extension is required before any session or directory is created.
    rejected_name = "blocked-" + secrets.token_hex(4) + ".exe"
    rejected = client.call(
        "POST", "/api/v1/uploads/init",
        init_request(pid, rejected_name, b"nope"), expected=400)
    check("file init rejects non-whitelisted extension", "文件类型" in rejected["message"])

    # A complete byte stream with the wrong declared MD5 must not produce a file.
    bad_md5_bytes = b"%PDF-1.4\nwrong digest contract\n%%EOF\n"
    bad = init_upload(
        client, pid, "bad-md5-" + secrets.token_hex(5) + ".pdf", bad_md5_bytes)
    bad_session = bad["sessionId"]
    try:
        put_chunk(client, bad_session, 0, bad_md5_bytes)
        missing_md5 = client.call(
            "POST", f"/api/v1/uploads/{bad_session}/merge", expected=409)
        check("file merge rejects a session before full MD5 submission",
              "摘要" in missing_md5["message"])
        submit_md5(client, bad_session, bad_md5_bytes, digest="0" * 32)
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
    missing = init_upload(
        client, pid, "missing-" + secrets.token_hex(5) + ".pdf", missing_bytes)
    missing_session = missing["sessionId"]
    try:
        check("file missing-chunk fixture uses multiple chunks", missing["totalChunks"] > 1)
        first_chunk = missing_bytes[:missing["chunkSize"]]
        put_chunk(client, missing_session, 0, first_chunk)
        submit_md5(client, missing_session, missing_bytes)
        incomplete = client.call("POST", f"/api/v1/uploads/{missing_session}/merge", expected=400)
        check("file merge rejects missing chunks", "分片不完整" in incomplete["message"])
    finally:
        _abort(client, missing_session)

    # Abort is idempotent, removes the session directory, and writes one audit event.
    abort_bytes = b"abort contract"
    abort_init = init_upload(
        client, pid, "abort-" + secrets.token_hex(5) + ".pdf", abort_bytes)
    abort_session = abort_init["sessionId"]
    with conn.cursor() as cursor:
        cursor.execute("SELECT temp_dir FROM upload_sessions WHERE id=%s", (abort_session,))
        abort_dir = Path(cursor.fetchone()[0])
    put_chunk(client, abort_session, 0, abort_bytes)
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
    # Destructive file endpoints deliberately collapse authorization failures to
    # 404 so callers cannot use them as a separate file-existence oracle.
    supplier_client.call("DELETE", f"/api/v1/files/{disposable_fid}", expected=404)
    with conn.cursor() as cursor:
        cursor.execute("SELECT status FROM files WHERE id=%s", (disposable_fid,))
        status_after_denial = cursor.fetchone()[0]
    check(
        "file delete permission is reflected and hidden-denial is side-effect free",
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

    # Preview and download are independent permissions. Inline documents have a
    # common 50 MiB cap; video uses a short-lived file-scoped cookie and Range stream.
    preview_client = _new_internal_with_permissions(
        client, conn, ["project:list", "project:view_all", "file:preview"])
    preview_pdf, _ = preview_client.call("GET", f"/api/v1/files/{fid}/content", raw=True)
    preview_client.call("GET", f"/api/v1/files/{fid}/download", expected=403)
    image_payloads = {
        "png": (b"\x89PNG\r\n\x1a\nimage-preview-png", "image/png"),
        "jpg": (b"\xff\xd8\xff\xe0image-preview-jpg\xff\xd9", "image/jpeg"),
        "jpeg": (b"\xff\xd8\xff\xe0image-preview-jpeg\xff\xd9", "image/jpeg"),
        "gif": (b"GIF89a\x01\x00\x01\x00image-preview-gif", "image/gif"),
        "webp": (b"RIFF\x10\x00\x00\x00WEBPimage-preview-webp", "image/webp"),
        "bmp": (b"BM\x1a\x00\x00\x00image-preview-bmp", "image/bmp"),
    }
    image_file_ids = {}
    image_responses = {}
    for extension, (content, expected_mime) in image_payloads.items():
        image_fid = _upload_file(client, pid, "preview." + extension, content)
        if image_fid is None:
            raise AssertionError("image merge response missing file identifier for " + extension)
        image_file_ids[extension] = image_fid
        served, served_headers = preview_client.call(
            "GET", f"/api/v1/files/{image_fid}/content", raw=True)
        image_responses[extension] = served == content and served_headers.get_content_type() == expected_mime
    check(
        "image previews return exact bytes and canonical MIME over authenticated HTTP",
        all(image_responses.values()),
    )

    no_preview_client = _new_internal_with_permissions(
        client, conn, ["project:list", "project:view_all"])
    no_preview_client.call(
        "GET", f"/api/v1/files/{image_file_ids['png']}/content", expected=403)
    check("image preview denies a project-visible user without file preview permission", True)

    out_of_scope_client = _new_internal_with_permissions(
        client, conn, ["project:list", "file:preview"])
    out_of_scope_client.call(
        "GET", f"/api/v1/files/{image_file_ids['png']}/content", expected=404)
    check("image preview hides file existence from a permitted user outside the project scope", True)

    zip_bytes = b"PK\x03\x04file preview boundary"
    zip_fid = _insert_available_file(
        conn, pid, uploader_id, storage_root, "zip", content=zip_bytes)
    oversized_xlsx_fid = _insert_available_file(
        conn, pid, uploader_id, storage_root, "xlsx", size=50 * 1024 * 1024 + 1)
    pptx_bytes = b"PK\x03\x04pptx preview contract"
    pptx_fid = _insert_available_file(
        conn, pid, uploader_id, storage_root, "pptx", content=pptx_bytes)
    pptx_preview, pptx_headers = preview_client.call(
        "GET", f"/api/v1/files/{pptx_fid}/content", raw=True)
    preview_client.call("GET", f"/api/v1/files/{zip_fid}/content", expected=400)
    preview_client.call("GET", f"/api/v1/files/{oversized_xlsx_fid}/content", expected=400)
    downloaded_zip, _ = client.call("GET", f"/api/v1/files/{zip_fid}/download", raw=True)
    check(
        "file preview permission enforces type size and independent download boundaries",
        preview_pdf.startswith(b"%PDF")
        and pptx_preview == pptx_bytes
        and pptx_headers.get_content_type() == "application/vnd.openxmlformats-officedocument.presentationml.presentation"
        and downloaded_zip == zip_bytes,
    )

    oversized_video_fid = _insert_available_file(
        conn, pid, uploader_id, storage_root, "mp4", size=50 * 1024 * 1024 + 1)
    media_session_bytes, media_session_headers = preview_client.call(
        "POST", f"/api/v1/files/{oversized_video_fid}/media-session", raw=True)
    media_session = json.loads(media_session_bytes)
    media_cookie = media_session_headers.get("Set-Cookie", "")
    media_cookie_lower = media_cookie.lower()
    access_token = preview_client.token
    preview_client.token = None
    video_range, video_headers = preview_client.call(
        "GET", media_session["url"], expected=206,
        headers={"Range": "bytes=0-7"}, raw=True)
    check(
        "video media grant streams ranges without bearer or document size limit",
        media_session["expiresInSeconds"] == 300
        and media_session["url"] == f"/api/v1/files/{oversized_video_fid}/media"
        and "httponly" in media_cookie_lower
        and "samesite=strict" in media_cookie_lower
        and f"path=/api/v1/files/{oversized_video_fid}/media" in media_cookie_lower
        and "max-age=300" in media_cookie_lower
        and len(video_range) == 8
        and video_headers.get_content_type() == "video/mp4"
        and video_headers.get("Content-Range", "").startswith("bytes 0-7/"),
    )
    preview_client.token = access_token
    preview_profile = preview_client.call("GET", "/api/v1/auth/profile")
    with conn.cursor() as cursor:
        cursor.execute("SELECT role_id FROM user_roles WHERE user_id=%s", (preview_profile["user"]["id"],))
        preview_role_id = cursor.fetchone()[0]
        cursor.execute("SELECT id FROM permissions WHERE code='file:preview'")
        preview_permission_id = cursor.fetchone()[0]
        cursor.execute("DELETE FROM role_permissions WHERE role_id=%s AND permission_id=%s",
                       (preview_role_id, preview_permission_id))
    preview_client.token = None
    preview_client.call("GET", media_session["url"], expected=403)
    with conn.cursor() as cursor:
        cursor.execute("INSERT INTO role_permissions(role_id,permission_id) VALUES(%s,%s)",
                       (preview_role_id, preview_permission_id))
    check("video media range rechecks current preview permission", True)
    preview_client.token = access_token
    preview_client.call("POST", "/api/v1/auth/logout")
    preview_client.token = None
    preview_client.call("GET", media_session["url"], expected=401)
    check("video media grant is revoked with its login session", True)

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


def run_upload_material_checks(client, conn, check, supplier_id):
    """Company-to-supplier material rules (docs/上传资料要求契约-2026-09-24.md) on a fresh subproject."""
    _, project = _create_project_group(client, client, conn, supplier_id, "上传资料要求-" + secrets.token_hex(5))
    project_id = project["id"]
    client.call("PUT", f"/api/v1/projects/{project_id}/status", {"status": "IN_PROGRESS"})
    pdf = b"%PDF-1.4\nupload material rules\n%%EOF\n"
    step = b"ISO-10303-21; upload material rules"
    workbook_name = "CSLR-605 放板機 2105931-1 動作流程.xlsx"
    workbook = b"PK\x03\x04 upload material workbook"

    missing_step = client.call(
        "POST", "/api/v1/uploads/init", init_request(project_id, "说明.pdf", pdf), expected=400)
    misnamed = client.call(
        "POST", "/api/v1/uploads/init", init_request(project_id, "动作流程.xlsx", workbook), expected=400)
    check("company upload without any STEP 3D drawing is rejected before a session exists",
          missing_step.get("code") == 40001 and "STEP" in missing_step["message"])
    check("company Excel upload must follow the CSLR motion-flow naming rule",
          misnamed.get("code") == 40001 and "CSLR-XXX XXX机 210XXX-X 动作流程.xlsx" in misnamed["message"])

    step_session = client.call("POST", "/api/v1/uploads/init", init_request(project_id, "装配.step", step))
    same_batch = client.call("POST", "/api/v1/uploads/init", init_request(project_id, workbook_name, workbook))
    check("an in-flight STEP session admits a correctly named same-batch workbook",
          bool(step_session["sessionId"]) and bool(same_batch["sessionId"]))
    # 2026-10-06: .xls/.xlsm/.xlsb follow the same naming rule as .xlsx and are in the default allowlist.
    for extension in ("xls", "xlsm", "xlsb"):
        other = client.call("POST", "/api/v1/uploads/init",
                            init_request(project_id, workbook_name[:-4] + extension, workbook))
        rejected = client.call("POST", "/api/v1/uploads/init",
                               init_request(project_id, "动作流程." + extension, workbook), expected=400)
        check(f"company .{extension} workbook is accepted when named like .xlsx and rejected otherwise",
              bool(other["sessionId"]) and rejected.get("code") == 40001
              and "CSLR-XXX XXX机 210XXX-X 动作流程.xlsx" in rejected["message"])
        _abort(client, other["sessionId"])
    _abort(client, same_batch["sessionId"])
    _abort(client, step_session["sessionId"])
    client.call("POST", "/api/v1/uploads/init", init_request(project_id, "说明.pdf", pdf), expected=400)

    _upload_file(client, project_id, "装配.stp", step)
    accepted = client.call("POST", "/api/v1/uploads/init", init_request(project_id, "说明.pdf", pdf))
    _abort(client, accepted["sessionId"])
    check("an aborted STEP session no longer counts and an available STEP file admits other material",
          bool(accepted["sessionId"]))
