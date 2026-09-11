//! Batch and single-file download path boundary regression tests.
//! Must be run through scripts/test-isolated.py against a disposable MySQL database.

use crate::{
    entity::{enums::FileStatus, files},
    error::AppError,
    regression::Fixture,
    service,
};
use sea_orm::{ActiveModelTrait, EntityTrait, Set};
use std::path::{Path, PathBuf};

struct OwnedTempDir(PathBuf);

impl Drop for OwnedTempDir {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}

fn assert_illegal_storage_path<T>(result: Result<T, AppError>, operation: &str) {
    assert!(
        matches!(result, Err(AppError::Internal(message)) if message == "非法存储路径"),
        "{operation} must reject a real file outside the configured storage root"
    );
}

#[test]
fn batch_download_deduplicates_ids_and_enforces_total_input_size() {
    assert_eq!(
        service::file::unique_batch_ids(&[7, 3, 7, 9, 3]),
        vec![7, 3, 9]
    );
    assert_eq!(
        service::file::checked_batch_input_size([128 * 1024 * 1024, 128 * 1024 * 1024]).unwrap(),
        service::file::BATCH_INPUT_MAX_BYTES
    );
    assert!(matches!(
        service::file::checked_batch_input_size([
            service::file::BATCH_INPUT_MAX_BYTES,
            1,
        ]),
        Err(AppError::BadRequest(message)) if message.contains("256 MiB")
    ));
}

#[test]
fn batch_download_limiter_enforces_user_global_and_temp_budgets() {
    let limiter = service::file::BatchLimiter::new(2, 800);
    let first = limiter.try_acquire(10, 400).unwrap();
    assert!(matches!(
        limiter.try_acquire(10, 1),
        Err(AppError::Conflict(message)) if message.contains("当前账号")
    ));
    let second = limiter.try_acquire(20, 400).unwrap();
    assert!(matches!(
        limiter.try_acquire(30, 1),
        Err(AppError::Conflict(message)) if message.contains("任务繁忙")
    ));
    drop(first);
    assert!(limiter.try_acquire(30, 401).is_err());
    drop(second);
    assert!(limiter.try_acquire(30, 401).is_ok());
}

#[test]
fn pdf_preview_size_gate_applies_only_to_large_pdfs() {
    assert!(
        service::file::ensure_pdf_preview_size("PDF", service::file::PDF_PREVIEW_MAX_BYTES,)
            .is_ok()
    );
    assert!(matches!(
        service::file::ensure_pdf_preview_size(
            "pdf",
            service::file::PDF_PREVIEW_MAX_BYTES + 1,
        ),
        Err(AppError::BadRequest(message)) if message.contains("下载原文件")
    ));
    assert!(service::file::ensure_pdf_preview_size(
        "bin",
        service::file::PDF_PREVIEW_MAX_BYTES + 1,
    )
    .is_ok());
}

#[tokio::test]
async fn batch_archive_body_removes_temp_file_on_completion_and_cancellation() {
    for consume in [true, false] {
        let path = std::env::temp_dir().join(format!(
            "yf_batch_cleanup_{}_{}.zip",
            if consume { "complete" } else { "cancel" },
            uuid::Uuid::new_v4().simple()
        ));
        std::fs::write(&path, b"controlled-test-zip").unwrap();
        let body = service::file::archive_body_for_test(path.clone()).await;
        if consume {
            assert_eq!(
                axum::body::to_bytes(body, 1024).await.unwrap(),
                "controlled-test-zip"
            );
        } else {
            drop(body);
        }
        assert!(
            !path.exists(),
            "temporary archive must be deleted when its response ends"
        );
    }
}

#[test]
fn failed_batch_archive_removes_partial_temp_file() {
    let output = std::env::temp_dir().join(format!(
        "yf_batch_failed_{}.zip",
        uuid::Uuid::new_v4().simple()
    ));
    let missing = output.with_extension("missing-source");
    assert!(service::file::failed_archive_for_test(output.clone(), missing).is_err());
    assert!(
        !output.exists(),
        "a failed ZIP build must remove its partial output"
    );
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn downloads_reject_real_files_outside_storage_root() {
    let fixture = Fixture::new().await;
    let session_id = fixture.init("boundary.pdf").await;
    service::upload::put_chunk(&fixture.state, &fixture.member, &session_id, 0, b"safe")
        .await
        .unwrap();
    let merged = service::upload::merge(&fixture.state, &fixture.member, &session_id)
        .await
        .unwrap();
    let file_id = merged["id"].as_u64().unwrap();

    let single = service::file::stream_file(&fixture.state, &fixture.member, file_id, false)
        .await
        .expect("a stored file inside the configured root must download");
    assert_eq!(single.headers()["cache-control"], "private, no-store");
    assert_eq!(
        axum::body::to_bytes(single.into_body(), 1024)
            .await
            .unwrap(),
        "safe"
    );
    let preview = service::file::stream_file(&fixture.state, &fixture.member, file_id, true)
        .await
        .expect("a small PDF inside the storage root must preview");
    assert_eq!(preview.headers()["cache-control"], "private, no-store");
    assert_eq!(
        axum::body::to_bytes(preview.into_body(), 1024)
            .await
            .unwrap(),
        "safe"
    );

    let row = files::Entity::find_by_id(file_id)
        .one(&fixture.state.db)
        .await
        .unwrap()
        .unwrap();
    let mut oversized: files::ActiveModel = row.into();
    oversized.size_bytes = Set(service::file::PDF_PREVIEW_MAX_BYTES + 1);
    oversized.update(&fixture.state.db).await.unwrap();
    assert!(matches!(
        service::file::stream_file(&fixture.state, &fixture.member, file_id, true).await,
        Err(AppError::BadRequest(message)) if message.contains("下载原文件")
    ));
    let unrestricted_download =
        service::file::stream_file(&fixture.state, &fixture.member, file_id, false)
            .await
            .expect("the preview size gate must not restrict ordinary download");
    assert_eq!(
        axum::body::to_bytes(unrestricted_download.into_body(), 1024)
            .await
            .unwrap(),
        "safe"
    );
    let row = files::Entity::find_by_id(file_id)
        .one(&fixture.state.db)
        .await
        .unwrap()
        .unwrap();
    let mut restored: files::ActiveModel = row.into();
    restored.size_bytes = Set(4);
    restored.update(&fixture.state.db).await.unwrap();

    let batch = service::file::batch_download(
        &fixture.state,
        &fixture.member,
        &service::file::BatchDownloadReq {
            ids: vec![file_id, file_id],
        },
    )
    .await
    .expect("a stored file inside the configured root must be packaged");
    assert_eq!(batch.headers()["cache-control"], "private, no-store");
    let bytes = axum::body::to_bytes(batch.into_body(), 1024 * 1024)
        .await
        .unwrap();
    let mut archive = zip::ZipArchive::new(std::io::Cursor::new(bytes)).unwrap();
    assert_eq!(archive.len(), 1, "duplicate IDs must produce one ZIP entry");
    let mut entry = archive.by_index(0).unwrap();
    let mut body = String::new();
    std::io::Read::read_to_string(&mut entry, &mut body).unwrap();
    assert_eq!(entry.name(), "boundary.pdf");
    assert_eq!(body, "safe");

    let storage_root = Path::new(&fixture.state.cfg.storage.root);
    let storage_parent = storage_root.parent().unwrap();
    let outside_dir = storage_parent.join(format!(
        "yf_batch_path_outside_{}",
        uuid::Uuid::new_v4().simple()
    ));
    let outside_dir = OwnedTempDir(outside_dir);
    std::fs::create_dir_all(&outside_dir.0).unwrap();
    let outside_file = outside_dir.0.join("outside-secret.pdf");
    std::fs::write(&outside_file, b"outside-secret").unwrap();
    assert!(
        outside_file.is_file(),
        "the escape target must really exist"
    );

    let relative_escape = PathBuf::from("..")
        .join(outside_dir.0.file_name().unwrap())
        .join(outside_file.file_name().unwrap());
    let absolute_escape = outside_file.clone();
    for (kind, storage_path) in [
        ("parent traversal", relative_escape.clone()),
        ("absolute path", absolute_escape),
    ] {
        let row = files::Entity::find_by_id(file_id)
            .one(&fixture.state.db)
            .await
            .unwrap()
            .unwrap();
        let mut update: files::ActiveModel = row.into();
        update.storage_path = Set(storage_path.to_string_lossy().into_owned());
        update.update(&fixture.state.db).await.unwrap();

        assert_illegal_storage_path(
            service::file::stream_file(&fixture.state, &fixture.member, file_id, false).await,
            &format!("single download with {kind}"),
        );
        assert_illegal_storage_path(
            service::file::batch_download(
                &fixture.state,
                &fixture.member,
                &service::file::BatchDownloadReq { ids: vec![file_id] },
            )
            .await,
            &format!("batch download with {kind}"),
        );
        assert_eq!(
            std::fs::read(&outside_file).unwrap(),
            b"outside-secret",
            "the rejection must not mutate the outside file"
        );
    }

    let row = files::Entity::find_by_id(file_id)
        .one(&fixture.state.db)
        .await
        .unwrap()
        .unwrap();
    let mut update: files::ActiveModel = row.into();
    update.storage_path = Set(relative_escape.to_string_lossy().into_owned());
    update.status = Set(FileStatus::Deleted);
    update.deleted_at = Set(Some(chrono::Utc::now() - chrono::Duration::days(31)));
    update.update(&fixture.state.db).await.unwrap();
    service::gc::run_all(&fixture.state).await;
    assert_eq!(std::fs::read(&outside_file).unwrap(), b"outside-secret");
    assert!(
        files::Entity::find_by_id(file_id)
            .one(&fixture.state.db)
            .await
            .unwrap()
            .is_some(),
        "GC must retain a rejected database row instead of deleting an outside file"
    );
}
