"""Create blob-backed file rows for caller-owned isolated HTTP fixtures."""

import hashlib
from pathlib import Path
import secrets


_BUFFER_SIZE = 1024 * 1024


def _zero_digest(size):
    digest = hashlib.sha256()
    block = bytes(_BUFFER_SIZE)
    remaining = size
    while remaining:
        count = min(remaining, len(block))
        digest.update(block[:count])
        remaining -= count
    return digest.hexdigest()


def _file_digest(path):
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        while chunk := stream.read(_BUFFER_SIZE):
            digest.update(chunk)
    return digest.hexdigest()


def insert_blob_file(
    conn,
    storage_root,
    project_id,
    uploader_id,
    original_name,
    extension,
    mime_type,
    *,
    content=None,
    size=None,
):
    if (content is None) == (size is None):
        raise ValueError("provide exactly one of content or size")
    physical_size = len(content) if content is not None else size
    digest = hashlib.sha256(content).hexdigest() if content is not None else _zero_digest(size)
    relative_path = f"blobs/sha256/{digest[:2]}/{digest[2:4]}/{digest}"
    path = Path(storage_root) / Path(relative_path)
    path.parent.mkdir(parents=True, exist_ok=True)
    if not path.exists():
        if content is not None:
            path.write_bytes(content)
        else:
            with path.open("wb") as stream:
                stream.truncate(size)
    if path.stat().st_size != physical_size:
        raise AssertionError("canonical blob fixture size mismatch")
    if _file_digest(path) != digest:
        raise AssertionError("canonical blob fixture digest mismatch")

    stored_name = secrets.token_hex(16) + "." + extension
    with conn.cursor() as cursor:
        cursor.execute(
            "INSERT INTO file_blobs(sha256,size_bytes,storage_path,state,gc_started_at,created_at) "
            "VALUES(%s,%s,%s,'READY',NULL,UTC_TIMESTAMP(6)) "
            "ON DUPLICATE KEY UPDATE id=LAST_INSERT_ID(id)",
            (digest, physical_size, relative_path),
        )
        blob_id = cursor.lastrowid
        cursor.execute(
            "INSERT INTO files(blob_id,project_id,uploader_id,direction,original_name,stored_name,ext,"
            "size_bytes,mime_type,sha256,storage_path,status,deleted_at,created_at) "
            "VALUES(%s,%s,%s,'C2S',%s,%s,%s,%s,%s,%s,%s,'AVAILABLE',NULL,UTC_TIMESTAMP(3))",
            (blob_id, project_id, uploader_id, original_name, stored_name, extension,
             physical_size, mime_type, digest, relative_path),
        )
        return {
            "file_id": cursor.lastrowid,
            "blob_id": blob_id,
            "sha256": digest,
            "storage_path": relative_path,
            "path": path,
        }
