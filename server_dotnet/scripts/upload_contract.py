"""Shared HTTP fixtures for the resumable upload v2 contract."""

import hashlib
import json


FINGERPRINT_SAMPLE_SIZE = 1024 * 1024
FIXTURE_LAST_MODIFIED = 1_700_000_000_000


def file_fingerprint(file_name, content, file_last_modified=FIXTURE_LAST_MODIFIED):
    first = content[:FINGERPRINT_SAMPLE_SIZE]
    last = content[max(0, len(content) - FINGERPRINT_SAMPLE_SIZE):]
    canonical = json.dumps(
        [
            "yf-upload-fingerprint-v2",
            file_name,
            len(content),
            file_last_modified,
            hashlib.sha256(first).hexdigest(),
            hashlib.sha256(last).hexdigest(),
        ],
        ensure_ascii=False,
        separators=(",", ":"),
    ).encode("utf-8")
    return hashlib.sha256(canonical).hexdigest()


def init_request(project_id, file_name, content, file_last_modified=FIXTURE_LAST_MODIFIED):
    return {
        "projectId": project_id,
        "fileName": file_name,
        "fileSize": len(content),
        "fileLastModified": file_last_modified,
        "fileFingerprint": file_fingerprint(file_name, content, file_last_modified),
    }


# 公司内部发给供应商的非 STEP 文件要求子项目已有 STEP 3D 图（上传资料要求契约-2026-09-24）。
# 夹具默认不做任何补救：规则拒绝会原样抛出。只有显式传 ensure_step=True 的调用方（内部账号向
# 可能还没有 STEP 的子项目上传非 STEP 夹具）才会在遇到该拒绝时先补传一个 STEP 文件再重试；
# 规则本身在 test_file_contracts.run_upload_material_checks 中显式验证。
STEP_REQUIRED_MARKER = "至少需要一个 STEP 格式 3D 图"
COMPANY_STEP_FIXTURE_NAME = "fixture-assembly.step"
COMPANY_STEP_FIXTURE_BYTES = b"ISO-10303-21; fixture assembly"


def init_upload(client, project_id, file_name, content, file_last_modified=FIXTURE_LAST_MODIFIED,
                *, ensure_step=False):
    request = init_request(project_id, file_name, content, file_last_modified)
    try:
        return client.call("POST", "/api/v1/uploads/init", request)
    except AssertionError as error:
        if not ensure_step or STEP_REQUIRED_MARKER not in str(error):
            raise
    upload_bytes(client, project_id, COMPANY_STEP_FIXTURE_NAME, COMPANY_STEP_FIXTURE_BYTES)
    return client.call("POST", "/api/v1/uploads/init", request)


def put_chunk(client, session_id, index, content, expected=200, declared_sha256=None):
    digest = declared_sha256 or hashlib.sha256(content).hexdigest()
    return client.call(
        "PUT",
        f"/api/v1/uploads/{session_id}/chunks/{index}",
        content,
        expected=expected,
        headers={
            "Content-Type": "application/octet-stream",
            "X-Chunk-SHA256": digest,
        },
    )


def submit_md5(client, session_id, content, digest=None, expected=200):
    return client.call(
        "POST",
        f"/api/v1/uploads/{session_id}/md5",
        {"fileMd5": digest or hashlib.md5(content).hexdigest()},
        expected=expected,
    )


def upload_bytes(client, project_id, file_name, content, *, ensure_step=False):
    initialized = init_upload(client, project_id, file_name, content, ensure_step=ensure_step)
    chunk_size = initialized["chunkSize"]
    for index in range(initialized["totalChunks"]):
        put_chunk(
            client,
            initialized["sessionId"],
            index,
            content[index * chunk_size:(index + 1) * chunk_size],
        )
    state = client.call("GET", f"/api/v1/uploads/{initialized['sessionId']}")
    submit_md5(client, initialized["sessionId"], content)
    merged = client.call("POST", f"/api/v1/uploads/{initialized['sessionId']}/merge")
    return initialized, state, merged
