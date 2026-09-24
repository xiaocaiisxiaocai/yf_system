"""Durable copy contracts on caller-owned local HTTP/database fixtures only."""
import hashlib
import secrets
import time

from test_business_acceptance import _create_project_group, _upload_chunks


def wait_for_copy(client, job, timeout=40):
    deadline = time.monotonic() + timeout
    previous_files = previous_bytes = 0
    while True:
        assert job["status"] in {"pending", "running", "succeeded", "failed"}
        assert 0 <= job["filesCopied"] <= job["filesTotal"]
        assert 0 <= job["bytesCopied"] <= job["bytesTotal"]
        assert job["filesCopied"] >= previous_files and job["bytesCopied"] >= previous_bytes
        previous_files, previous_bytes = job["filesCopied"], job["bytesCopied"]
        if job["status"] in {"succeeded", "failed"}:
            return job
        if time.monotonic() >= deadline:
            raise AssertionError(f"copy job {job['jobId']} did not finish within {timeout}s")
        time.sleep(0.1)
        job = client.call("GET", f"/api/v1/project-copy-jobs/{job['jobId']}")


def submit_and_wait_copy(client, source_id, name):
    job = client.call("POST", f"/api/v1/projects/{source_id}/copy", {
        "name": name, "idempotencyKey": secrets.token_hex(16),
    }, expected=202)
    job = wait_for_copy(client, job)
    assert job["status"] == "succeeded", job.get("error")
    return job


def run_background_copy_checks(client, conn, check, supplier_id):
    group, source = _create_project_group(
        client, client, conn, supplier_id, "后台复制-" + secrets.token_hex(5))
    client.call("PUT", f"/api/v1/projects/{source['id']}/status", {"status": "IN_PROGRESS"})
    payload = b"%PDF-1.4\n" + b"background-copy-fixture\n" * 25000 + b"%%EOF\n"
    file_id, _, _ = _upload_chunks(client, source["id"], "background-copy.pdf", payload)
    assert file_id is not None
    name = "后台复制目标-" + secrets.token_hex(5)
    request = {"name": name, "idempotencyKey": secrets.token_hex(16)}
    job = client.call("POST", f"/api/v1/projects/{source['id']}/copy", request, expected=202)
    repeated = client.call("POST", f"/api/v1/projects/{source['id']}/copy", request, expected=202)
    check("background copy accepts a durable job and deduplicates a retried submission",
          job["jobId"] == repeated["jobId"] and job["sourceProjectId"] == source["id"]
          and job["projectGroupId"] == group["id"] and job["targetName"] == name)
    client.call("POST", f"/api/v1/projects/{source['id']}/copy",
                {**request, "name": name + "不同"}, expected=409)
    check("background copy idempotency key cannot be reused for another target", True)
    final = wait_for_copy(client, repeated)
    check("background copy exposes a persisted successful result and complete progress",
          final["status"] == "succeeded" and final["error"] is None
          and final["filesCopied"] == final["filesTotal"] == 1
          and final["bytesCopied"] == final["bytesTotal"] == len(payload)
          and final["result"]["copyFileCount"] == 1 and final["completedAt"] is not None)
    target_id = final["result"]["projectId"]
    listed = client.call("GET", f"/api/v1/project-groups/{group['id']}/copy-jobs")
    restored = client.call("GET", f"/api/v1/project-copy-jobs/{job['jobId']}")
    check("background copy jobs remain discoverable after leaving the submit page",
          any(item["jobId"] == job["jobId"] for item in listed["jobs"])
          and restored["result"] == final["result"])
    again = client.call("POST", f"/api/v1/projects/{source['id']}/copy", request, expected=202)
    with conn.cursor() as cursor:
        cursor.execute("SELECT COUNT(*) FROM projects WHERE name=%s", (name,))
        target_count = cursor.fetchone()[0]
        cursor.execute(
            "SELECT id,project_id,sha256,storage_path,blob_id FROM files WHERE project_id=%s",
            (target_id,),
        )
        copied_file = cursor.fetchone()
        cursor.execute("SELECT storage_path,blob_id FROM files WHERE id=%s", (file_id,))
        source_path, source_blob_id = cursor.fetchone()
        cursor.execute("SELECT COUNT(*) FROM files WHERE blob_id=%s", (source_blob_id,))
        blob_references = cursor.fetchone()[0]
    content, _ = client.call("GET", f"/api/v1/files/{copied_file[0]}/download", raw=True)
    check("completed copy retries share one immutable blob without duplicating the project",
          again["jobId"] == job["jobId"] and target_count == 1
          and copied_file[0] != file_id
          and copied_file[1] == target_id != source["id"]
          and copied_file[2] == hashlib.sha256(payload).hexdigest()
          and copied_file[3] == source_path
          and copied_file[4] == source_blob_id
          and blob_references >= 2
          and content == payload)

    # Missing keys are now invalid; there is no synchronous legacy fallback.
    client.call("POST", f"/api/v1/projects/{source['id']}/copy", {"name": name + "旧请求"}, expected=400)
    check("copy requires the current idempotent async request contract", True)
