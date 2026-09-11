//! 定期清理：过期/中止的上传临时目录、残留的批量打包 zip、软删除文件、过期 refresh token、过期验证码。
//! 由 notify worker 的循环每 10 分钟驱动一次（见 worker.rs）。
use chrono::{Duration, Utc};
use sea_orm::{ColumnTrait, EntityTrait, QueryFilter};

use crate::entity::enums::{FileStatus, UploadStatus};
use crate::entity::{files, refresh_tokens, upload_sessions};
use crate::state::AppState;

/// 软删文件保留 30 天后物理删除；refresh token 原到期时间 7 天后清除；tmp zip 保留 1 天
const FILE_KEEP_DAYS: i64 = 30;
const TOKEN_KEEP_DAYS: i64 = 7;
const ZIP_KEEP_HOURS: i64 = 24;

pub async fn run_all(state: &AppState) {
    purge_deleted_files(state).await;
    purge_old_tokens(state).await;
    purge_tmp_zips(state).await;
    purge_orphan_upload_dirs(state).await;
    state
        .captchas
        .lock()
        .map(|mut m| m.retain(|_, (_, exp)| *exp > std::time::Instant::now()))
        .ok();
}

/// 软删除文件从实际删除时间起保留 30 天后物理删除。
async fn purge_deleted_files(state: &AppState) {
    let db = &state.db;
    let cutoff = Utc::now() - Duration::days(FILE_KEEP_DAYS);
    let doomed = files::Entity::find()
        .filter(files::Column::Status.eq(FileStatus::Deleted))
        .filter(files::Column::DeletedAt.is_not_null())
        .filter(files::Column::DeletedAt.lt(cutoff))
        .all(db)
        .await;
    let Ok(doomed) = doomed else { return };
    for f in doomed {
        let abs = std::path::Path::new(&state.cfg.storage.root).join(&f.storage_path);
        match crate::storage::canonical_existing_path(&state.cfg.storage.root, &abs).await {
            Ok(canonical_path) => {
                if let Err(e) = tokio::fs::remove_file(&canonical_path).await {
                    // 文件已不存在是正常情况，其他错误跳过该行下次再试
                    if e.kind() != std::io::ErrorKind::NotFound {
                        tracing::warn!(error = ?e, path = %canonical_path.display(), "清理软删文件失败");
                        continue;
                    }
                }
            }
            Err(crate::error::AppError::NotFound) => {}
            Err(e) => {
                tracing::warn!(error = ?e, path = %abs.display(), "拒绝清理存储根目录之外的文件");
                continue;
            }
        }
        if let Err(e) = files::Entity::delete_by_id(f.id).exec(db).await {
            tracing::warn!(error = ?e, id = f.id, "删除软删文件记录失败");
        }
    }
}

/// Keep rotated/revoked token hashes through their original validity window for replay detection.
async fn purge_old_tokens(state: &AppState) {
    let cutoff = Utc::now() - Duration::days(TOKEN_KEEP_DAYS);
    let r = refresh_tokens::Entity::delete_many()
        .filter(refresh_tokens::Column::ExpiresAt.lt(cutoff))
        .exec(&state.db)
        .await;
    if let Err(e) = r {
        tracing::warn!(error = ?e, "清理 refresh token 失败");
    }
}

/// 批量下载的临时 zip 超过 24 小时删除
async fn purge_tmp_zips(state: &AppState) {
    let tmp = std::path::Path::new(&state.cfg.storage.root).join("tmp");
    let cutoff =
        std::time::SystemTime::now() - Duration::hours(ZIP_KEEP_HOURS).to_std().unwrap_or_default();
    let mut rd = match tokio::fs::read_dir(&tmp).await {
        Ok(r) => r,
        Err(_) => return,
    };
    while let Ok(Some(entry)) = rd.next_entry().await {
        let name = entry.file_name().to_string_lossy().to_string();
        if !(name.starts_with("yf_files_") && name.ends_with(".zip")) {
            continue;
        }
        let old = entry
            .metadata()
            .await
            .and_then(|m| m.modified())
            .map(|t| t < cutoff)
            .unwrap_or(false);
        if old {
            let _ = tokio::fs::remove_file(entry.path()).await;
        }
    }
}

/// 无对应进行中会话的临时分片目录（进程崩溃/异常退出残留）：删除 24h 前修改的
async fn purge_orphan_upload_dirs(state: &AppState) {
    let db = &state.db;
    let tmp = std::path::Path::new(&state.cfg.storage.root).join("tmp");
    let cutoff = std::time::SystemTime::now() - Duration::hours(24).to_std().unwrap_or_default();
    // 上传或合并中的会话目录仍可能被请求使用，不能按孤儿目录清理。
    let active = upload_sessions::Entity::find()
        .filter(
            upload_sessions::Column::Status.is_in([UploadStatus::Uploading, UploadStatus::Merging]),
        )
        .all(db)
        .await;
    let Ok(active) = active else {
        tracing::warn!("查询活跃上传会话失败，跳过孤儿目录清理");
        return;
    };
    let active: Vec<String> = active.into_iter().map(|s| s.id).collect();
    let mut rd = match tokio::fs::read_dir(&tmp).await {
        Ok(r) => r,
        Err(_) => return,
    };
    while let Ok(Some(entry)) = rd.next_entry().await {
        let Ok(ft) = entry.file_type().await else {
            continue;
        };
        if !ft.is_dir() {
            continue;
        }
        let name = entry.file_name().to_string_lossy().to_string();
        if active.contains(&name) {
            continue;
        }
        let old = entry
            .metadata()
            .await
            .and_then(|m| m.modified())
            .map(|t| t < cutoff)
            .unwrap_or(false);
        if old {
            let _ = tokio::fs::remove_dir_all(entry.path()).await;
        }
    }
}
