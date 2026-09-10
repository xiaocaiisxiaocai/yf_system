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
    assert_eq!(
        axum::body::to_bytes(single.into_body(), 1024)
            .await
            .unwrap(),
        "safe"
    );
    let batch = service::file::batch_download(
        &fixture.state,
        &fixture.member,
        &service::file::BatchDownloadReq { ids: vec![file_id] },
    )
    .await
    .expect("a stored file inside the configured root must be packaged");
    let bytes = axum::body::to_bytes(batch.into_body(), 1024 * 1024)
        .await
        .unwrap();
    let mut archive = zip::ZipArchive::new(std::io::Cursor::new(bytes)).unwrap();
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
