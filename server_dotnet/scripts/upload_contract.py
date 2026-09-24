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


def init_upload(client, project_id, file_name, content, file_last_modified=FIXTURE_LAST_MODIFIED):
    return client.call(
        "POST",
        "/api/v1/uploads/init",
        init_request(project_id, file_name, content, file_last_modified),
    )


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


def upload_bytes(client, project_id, file_name, content):
    initialized = init_upload(client, project_id, file_name, content)
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
