//! 分片上传：init → chunks → merge（幂等）/ abort，断点续传靠扫描临时目录
use chrono::{Duration, Timelike, Utc};
use md5::Md5;
use sea_orm::{
    ActiveModelTrait, ColumnTrait, DatabaseConnection, DatabaseTransaction, EntityTrait,
    QueryFilter, QueryOrder, QuerySelect, Set, TransactionTrait,
};
use serde::Deserialize;
use serde_json::{json, Value};
use sha2::{Digest, Sha256};

use crate::entity::enums::{
    FileDirection, FileStatus, ProjectStatus, RoundStatus, UploadStatus, UserType,
};
use crate::entity::{files, projects, rounds, system_configs, upload_sessions};
use crate::error::{ApiResult, AppError};
use crate::middleware::auth::CurrentUser;
use crate::state::AppState;
use crate::storage;

use super::{audit, notify, scope};

#[cfg(test)]
tokio::task_local! {
    static LOSE_UPLOAD_COMMIT_CONFIRMATION: bool;
}

/// A COMMIT error is an unknown outcome: the server may have committed before
/// the connection lost its confirmation. Callers must reconcile fresh state
/// before removing files created ahead of the transaction.
async fn commit_upload_transaction(txn: DatabaseTransaction) -> ApiResult<()> {
    txn.commit().await?;
    #[cfg(test)]
    if LOSE_UPLOAD_COMMIT_CONFIRMATION
        .try_with(|enabled| *enabled)
        .unwrap_or(false)
    {
        return Err(AppError::Internal(
            "simulated lost upload commit confirmation".into(),
        ));
    }
    Ok(())
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct InitReq {
    pub project_id: u64,
    pub round_id: u64,
    pub file_name: String,
    pub file_size: u64,
    pub file_md5: Option<String>,
}

async fn cfg_value(db: &DatabaseConnection, key: &str) -> Option<String> {
    system_configs::Entity::find_by_id(key.to_string())
        .one(db)
        .await
        .ok()
        .flatten()
        .and_then(|c| c.cfg_value)
}

fn ext_of(name: &str) -> String {
    name.rsplit('.').next().unwrap_or("").to_ascii_lowercase()
}

async fn validate_file(
    db: &DatabaseConnection,
    state: &AppState,
    file_name: &str,
    file_size: u64,
) -> ApiResult<String> {
    if file_name.trim().is_empty()
        || file_name.chars().count() > 255
        || file_name
            .chars()
            .any(|c| c.is_control() || matches!(c, '/' | '\\'))
    {
        return Err(AppError::BadRequest(
            "文件名需为 1~255 个字符且不能包含路径或控制字符".into(),
        ));
    }
    if file_size == 0 {
        return Err(AppError::BadRequest("空文件不可上传".into()));
    }
    let max: u64 = cfg_value(db, "upload.max_file_size")
        .await
        .and_then(|v| v.parse().ok())
        .unwrap_or(state.cfg.upload.max_file_size);
    if file_size > max {
        return Err(AppError::BadRequest(format!(
            "文件超过大小上限 {} MB",
            max / 1024 / 1024
        )));
    }
    let ext = ext_of(file_name);
    let whitelist: Vec<String> = cfg_value(db, "upload.allowed_exts")
        .await
        .map(|v| v.split(',').map(|s| s.trim().to_string()).collect())
        .unwrap_or_default();
    if !whitelist.is_empty() && !whitelist.contains(&ext) {
        return Err(AppError::BadRequest(format!("不支持的文件类型 .{ext}")));
    }
    Ok(ext)
}

/// 轮次可写 = 项目 IN_PROGRESS 且轮次 PENDING
async fn ensure_round_writable(
    db: &DatabaseConnection,
    project_id: u64,
    round_id: u64,
) -> ApiResult<rounds::Model> {
    let project = crate::entity::projects::Entity::find_by_id(project_id)
        .one(db)
        .await?
        .ok_or(AppError::NotFound)?;
    if project.status != ProjectStatus::InProgress {
        return Err(AppError::Conflict(
            "项目需处于「进行中」才能上传文件".into(),
        ));
    }
    let r = rounds::Entity::find_by_id(round_id)
        .one(db)
        .await?
        .ok_or(AppError::BadRequest("轮次不存在".into()))?;
    if r.project_id != project_id {
        return Err(AppError::BadRequest("轮次不属于该项目".into()));
    }
    if r.status != RoundStatus::Pending {
        return Err(AppError::Conflict("该轮次已关闭，不可上传文件".into()));
    }
    Ok(r)
}

/// 与项目状态、轮次决定及成员调整共享数据库行锁。固定顺序：项目 → 轮次 → 上传会话。
/// 文件合并在事务外执行，只有最终写入和分片落盘持有这些锁。
async fn lock_writable_target(
    txn: &DatabaseTransaction,
    user: &CurrentUser,
    session: &upload_sessions::Model,
) -> ApiResult<(projects::Model, rounds::Model)> {
    lock_writable_ids(txn, user, session.project_id, session.round_id).await
}

async fn lock_writable_ids(
    txn: &DatabaseTransaction,
    user: &CurrentUser,
    project_id: u64,
    round_id: u64,
) -> ApiResult<(projects::Model, rounds::Model)> {
    let project = projects::Entity::find_by_id(project_id)
        .lock_exclusive()
        .one(txn)
        .await?
        .ok_or(AppError::NotFound)?;
    let round = rounds::Entity::find_by_id(round_id)
        .lock_exclusive()
        .one(txn)
        .await?
        .ok_or(AppError::NotFound)?;
    scope::ensure_project_access(txn, user, project.id).await?;
    if project.status != ProjectStatus::InProgress || round.status != RoundStatus::Pending {
        return Err(AppError::Conflict("项目或轮次已关闭，不可上传文件".into()));
    }
    if round.project_id != project.id {
        return Err(AppError::BadRequest("轮次不属于该项目".into()));
    }
    Ok((project, round))
}

/// 扫描临时目录，返回已上传且大小正确的分片序号
fn uploaded_chunks(root: &str, sid: &str, chunk_size: u32, total: u32, file_size: u64) -> Vec<u32> {
    let dir = storage::tmp_dir(root, sid);
    let mut ok = Vec::new();
    for i in 0..total {
        let p = storage::chunk_path(root, sid, i);
        if let Ok(meta) = std::fs::metadata(&p) {
            let expect = if i == total - 1 {
                file_size - chunk_size as u64 * (total as u64 - 1)
            } else {
                chunk_size as u64
            };
            if meta.len() == expect {
                ok.push(i);
            }
        }
    }
    let _ = dir;
    ok
}

pub async fn init(state: &AppState, user: &CurrentUser, req: &InitReq) -> ApiResult<Value> {
    let db = &state.db;
    scope::ensure_project_access(db, user, req.project_id).await?;
    ensure_round_writable(db, req.project_id, req.round_id).await?;
    let ext = validate_file(db, state, &req.file_name, req.file_size).await?;
    if let Some(md5) = &req.file_md5 {
        if md5.len() != 32 || !md5.bytes().all(|b| b.is_ascii_hexdigit()) {
            return Err(AppError::BadRequest("文件 MD5 摘要格式无效".into()));
        }
    }

    let chunk_size = cfg_value(db, "upload.chunk_size")
        .await
        .and_then(|v| v.parse().ok())
        .unwrap_or(state.cfg.upload.chunk_size);
    // 防御非法配置：0 会导致 div_ceil 除零 panic，过小会产生海量分片
    let chunk_size = chunk_size.clamp(256 * 1024, 64 * 1024 * 1024);
    let total_chunks = req.file_size.div_ceil(chunk_size as u64) as u32;

    // 自动续传必须有内容摘要；旧客户端省略摘要时创建新会话，避免混用同名同大小文件的分片。
    let existing = if let Some(md5) = &req.file_md5 {
        upload_sessions::Entity::find()
            .filter(upload_sessions::Column::ProjectId.eq(req.project_id))
            .filter(upload_sessions::Column::RoundId.eq(req.round_id))
            .filter(upload_sessions::Column::UploaderId.eq(user.id))
            .filter(upload_sessions::Column::FileName.eq(req.file_name.clone()))
            .filter(upload_sessions::Column::FileSize.eq(req.file_size))
            .filter(upload_sessions::Column::FileMd5.eq(md5))
            .filter(upload_sessions::Column::ExpiresAt.gt(Utc::now()))
            .filter(
                upload_sessions::Column::Status
                    .is_in([UploadStatus::Uploading, UploadStatus::Merging]),
            )
            .order_by_desc(upload_sessions::Column::CreatedAt)
            .one(db)
            .await?
    } else {
        None
    };
    if let Some(mut existing) = existing {
        if existing.status == UploadStatus::Merging {
            if existing.updated_at > Utc::now() - Duration::minutes(10) {
                return Err(AppError::Conflict("该文件正在合并，请稍候".into()));
            }
            // 上次进程若在合并期间退出，正式完成状态会与文件记录同事务提交；无结果的旧 MERGING 可安全重试。
            let txn = db.begin().await?;
            super::perm::lock_business_state(&txn).await?;
            lock_writable_target(&txn, user, &existing).await?;
            let current = upload_sessions::Entity::find_by_id(existing.id.clone())
                .lock_exclusive()
                .one(&txn)
                .await?
                .ok_or(AppError::NotFound)?;
            super::perm::recheck_manager(&txn, user.id, "file:upload").await?;
            if current.status != UploadStatus::Merging || current.updated_at != existing.updated_at
            {
                return Err(AppError::Conflict("会话已变更，请重试".into()));
            }
            let reset = upload_sessions::ActiveModel {
                status: Set(UploadStatus::Uploading),
                updated_at: Set(Utc::now()),
                ..Default::default()
            };
            let result = upload_sessions::Entity::update_many()
                .set(reset)
                .filter(upload_sessions::Column::Id.eq(&existing.id))
                .filter(upload_sessions::Column::Status.eq(UploadStatus::Merging))
                .filter(upload_sessions::Column::UpdatedAt.eq(existing.updated_at))
                .exec(&txn)
                .await?;
            if result.rows_affected != 1 {
                return Err(AppError::Conflict("会话已变更，请重试".into()));
            }
            txn.commit().await?;
            existing = load_session(db, &existing.id).await?;
        }
        let uploaded = uploaded_chunks(
            &state.cfg.storage.root,
            &existing.id,
            existing.chunk_size,
            existing.total_chunks,
            existing.file_size,
        );
        return Ok(json!({
            "sessionId": existing.id,
            "chunkSize": existing.chunk_size,
            "totalChunks": existing.total_chunks,
            "uploadedChunks": uploaded,
            "resumed": true,
        }));
    }

    let sid = uuid::Uuid::new_v4().to_string();
    let tmp_dir = storage::tmp_dir(&state.cfg.storage.root, &sid);
    tokio::fs::create_dir_all(&tmp_dir)
        .await
        .map_err(|e| AppError::Internal(format!("创建临时目录失败: {e}")))?;

    let now = Utc::now();
    let prepared: ApiResult<DatabaseTransaction> = async {
        let txn = db.begin().await?;
        super::perm::lock_business_state(&txn).await?;
        lock_writable_ids(&txn, user, req.project_id, req.round_id).await?;
        super::perm::recheck_manager(&txn, user.id, "file:upload").await?;
        upload_sessions::ActiveModel {
            id: Set(sid.clone()),
            project_id: Set(req.project_id),
            round_id: Set(req.round_id),
            uploader_id: Set(user.id),
            file_name: Set(req.file_name.clone()),
            file_size: Set(req.file_size),
            file_md5: Set(req.file_md5.clone()),
            chunk_size: Set(chunk_size),
            total_chunks: Set(total_chunks),
            temp_dir: Set(tmp_dir.to_string_lossy().to_string()),
            status: Set(UploadStatus::Uploading),
            result_file_id: Set(None),
            expires_at: Set(now + Duration::hours(24)),
            created_at: Set(now),
            updated_at: Set(now),
        }
        .insert(&txn)
        .await?;
        Ok(txn)
    }
    .await;
    let txn = match prepared {
        Ok(txn) => txn,
        Err(e) => {
            let _ = tokio::fs::remove_dir_all(&tmp_dir).await;
            return Err(e);
        }
    };
    if let Err(commit_error) = commit_upload_transaction(txn).await {
        match upload_sessions::Entity::find_by_id(sid.clone())
            .one(db)
            .await
        {
            Ok(Some(_)) => {
                tracing::warn!(session_id = %sid, error = ?commit_error, "上传会话提交确认丢失，已按会话记录确认成功");
            }
            Ok(None) => {
                tracing::warn!(session_id = %sid, error = ?commit_error, "上传会话提交结果未知，保留临时目录待核对");
                return Err(commit_error);
            }
            Err(confirm_error) => {
                tracing::warn!(session_id = %sid, error = ?commit_error, confirmation_error = ?confirm_error, "无法确认上传会话提交结果，保留临时目录待核对");
                return Err(commit_error);
            }
        }
    }
    let _ = ext;

    Ok(json!({
        "sessionId": sid,
        "chunkSize": chunk_size,
        "totalChunks": total_chunks,
        "uploadedChunks": Vec::<u32>::new(),
    }))
}

async fn load_session(db: &DatabaseConnection, sid: &str) -> ApiResult<upload_sessions::Model> {
    let s = upload_sessions::Entity::find_by_id(sid.to_string())
        .one(db)
        .await?
        .ok_or(AppError::NotFound)?;
    if s.expires_at < Utc::now() && s.status == UploadStatus::Uploading {
        return Err(AppError::Conflict("上传会话已过期，请重新发起".into()));
    }
    Ok(s)
}

pub async fn put_chunk(
    state: &AppState,
    user: &CurrentUser,
    sid: &str,
    index: u32,
    body: &[u8],
) -> ApiResult<()> {
    let s = load_session(&state.db, sid).await?;
    if s.uploader_id != user.id {
        return Err(AppError::Forbidden);
    }
    let txn = state.db.begin().await?;
    super::perm::lock_business_state(&txn).await?;
    lock_writable_target(&txn, user, &s).await?;
    let s = upload_sessions::Entity::find_by_id(sid.to_string())
        .lock_exclusive()
        .one(&txn)
        .await?
        .ok_or(AppError::NotFound)?;
    if s.status != UploadStatus::Uploading {
        return Err(AppError::Conflict(
            "会话不可上传（可能已合并或放弃）".into(),
        ));
    }
    super::perm::recheck_manager(&txn, user.id, "file:upload").await?;
    if s.expires_at < Utc::now() {
        return Err(AppError::Conflict("上传会话已过期，请重新发起".into()));
    }
    if index >= s.total_chunks {
        return Err(AppError::BadRequest("分片序号越界".into()));
    }
    let expect = if index == s.total_chunks - 1 {
        s.file_size - s.chunk_size as u64 * (s.total_chunks as u64 - 1)
    } else {
        s.chunk_size as u64
    };
    if body.len() as u64 != expect {
        return Err(AppError::BadRequest(format!(
            "分片大小不符：期望 {expect}，实际 {}",
            body.len()
        )));
    }
    let path = storage::chunk_path(&state.cfg.storage.root, sid, index);
    if path.exists() {
        if tokio::fs::metadata(&path)
            .await
            .map(|m| m.len())
            .unwrap_or(0)
            == expect
        {
            txn.commit().await?;
            return Ok(()); // 幂等：同尺寸分片已完整落盘
        }
        tokio::fs::remove_file(&path)
            .await
            .map_err(|e| AppError::Internal(format!("清理损坏分片失败: {e}")))?;
    }
    let tmp = path.with_extension("part.tmp");
    tokio::fs::write(&tmp, body)
        .await
        .map_err(|e| AppError::Internal(format!("写分片失败: {e}")))?;
    tokio::fs::rename(&tmp, &path)
        .await
        .map_err(|e| AppError::Internal(format!("分片落盘失败: {e}")))?;
    txn.commit().await?;
    Ok(())
}

pub async fn status(state: &AppState, user: &CurrentUser, sid: &str) -> ApiResult<Value> {
    let s = load_session(&state.db, sid).await?;
    if s.uploader_id != user.id {
        return Err(AppError::Forbidden);
    }
    scope::ensure_project_access(&state.db, user, s.project_id).await?;
    let uploaded = uploaded_chunks(
        &state.cfg.storage.root,
        sid,
        s.chunk_size,
        s.total_chunks,
        s.file_size,
    );
    Ok(json!({
        "sessionId": s.id,
        "status": format!("{:?}", s.status).to_uppercase(),
        "chunkSize": s.chunk_size,
        "totalChunks": s.total_chunks,
        "uploadedChunks": uploaded,
        "fileName": s.file_name,
        "fileSize": s.file_size,
        "resultFileId": s.result_file_id,
    }))
}

pub async fn abort(state: &AppState, user: &CurrentUser, sid: &str) -> ApiResult<()> {
    let db = &state.db;
    let txn = db.begin().await?;
    super::perm::lock_business_state(&txn).await?;
    let s = upload_sessions::Entity::find_by_id(sid.to_string())
        .lock_exclusive()
        .one(&txn)
        .await?
        .ok_or(AppError::NotFound)?;
    if s.uploader_id != user.id {
        return Err(AppError::Forbidden);
    }
    super::perm::recheck_manager(&txn, user.id, "file:upload").await?;
    if matches!(s.status, UploadStatus::Completed | UploadStatus::Merging) {
        return Err(AppError::Conflict("会话已完成，不可放弃".into()));
    }
    let mut am: upload_sessions::ActiveModel = s.clone().into();
    am.status = Set(UploadStatus::Aborted);
    am.updated_at = Set(Utc::now());
    am.update(&txn).await?;
    audit::insert(
        &txn,
        Some(user.id),
        Some(user.employee_no.clone()),
        "UPLOAD_ABORT",
        Some("upload_session"),
        Some(sid.to_string()),
        None,
        None,
    )
    .await?;
    txn.commit().await?;
    let _ = tokio::fs::remove_dir_all(&s.temp_dir).await;
    Ok(())
}

pub async fn merge(state: &AppState, user: &CurrentUser, sid: &str) -> ApiResult<Value> {
    let db = &state.db;
    let s = load_session(db, sid).await?;
    if s.uploader_id != user.id {
        return Err(AppError::Forbidden);
    }
    scope::ensure_project_access(db, user, s.project_id).await?;
    // 幂等：已完成直接返回文件记录
    if s.status == UploadStatus::Completed {
        if let Some(file_id) = s.result_file_id {
            if let Some(f) = files::Entity::find_by_id(file_id).one(db).await? {
                return Ok(super::file::file_json(&f));
            }
        }
        if let Some(f) = files::Entity::find()
            .filter(files::Column::RoundId.eq(s.round_id))
            .filter(files::Column::OriginalName.eq(&s.file_name))
            .filter(files::Column::UploaderId.eq(user.id))
            .order_by_desc(files::Column::Id)
            .one(db)
            .await?
        {
            return Ok(super::file::file_json(&f));
        }
        return Err(AppError::Conflict("会话已完成".into()));
    }
    if s.status == UploadStatus::Merging {
        return Err(AppError::Conflict("正在合并中，请稍候".into()));
    }
    if s.status != UploadStatus::Uploading {
        return Err(AppError::Conflict("会话已失效".into()));
    }

    // 上传期间轮次可能已被确认/撤销，合并前复查可写性
    ensure_round_writable(db, s.project_id, s.round_id).await?;

    // 校验分片完整性
    let uploaded = uploaded_chunks(
        &state.cfg.storage.root,
        sid,
        s.chunk_size,
        s.total_chunks,
        s.file_size,
    );
    if uploaded.len() as u32 != s.total_chunks {
        return Err(AppError::BadRequest(format!(
            "分片不完整：已传 {}/{}",
            uploaded.len(),
            s.total_chunks
        )));
    }

    // CAS 进入 Merging：并发 merge 只有一个能成功，防止双写文件记录
    let merge_started = Utc::now().with_nanosecond(0).unwrap();
    let cas = upload_sessions::ActiveModel {
        status: Set(UploadStatus::Merging),
        updated_at: Set(merge_started),
        ..Default::default()
    };
    let upd = upload_sessions::Entity::update_many()
        .set(cas)
        .filter(upload_sessions::Column::Id.eq(sid))
        .filter(upload_sessions::Column::Status.eq(UploadStatus::Uploading))
        .exec(db)
        .await?;
    if upd.rows_affected == 0 {
        return Err(AppError::Conflict("合并已在进行中，请稍候".into()));
    }

    let mut s = s;
    s.updated_at = merge_started;
    let result = do_merge(state, user, &s).await;
    match result {
        Ok(file_json) => {
            let _ = tokio::fs::remove_dir_all(&s.temp_dir).await;
            Ok(file_json)
        }
        Err(e) => {
            // 回滚为 UPLOADING，允许重试 merge
            let am = upload_sessions::ActiveModel {
                status: Set(UploadStatus::Uploading),
                updated_at: Set(Utc::now()),
                ..Default::default()
            };
            upload_sessions::Entity::update_many()
                .set(am)
                .filter(upload_sessions::Column::Id.eq(sid))
                .filter(upload_sessions::Column::Status.eq(UploadStatus::Merging))
                .filter(upload_sessions::Column::UpdatedAt.eq(merge_started))
                .exec(db)
                .await?;
            Err(e)
        }
    }
}

async fn do_merge(
    state: &AppState,
    user: &CurrentUser,
    s: &upload_sessions::Model,
) -> ApiResult<Value> {
    let db = &state.db;
    let ext = ext_of(&s.file_name);
    let stored_name = format!("{}.{}", uuid::Uuid::new_v4(), ext);
    let now = Utc::now();
    let final_path = storage::final_path(&state.cfg.storage.root, &now, &stored_name);
    storage::ensure_within_root(&state.cfg.storage.root, &final_path)?;
    if let Some(parent) = final_path.parent() {
        tokio::fs::create_dir_all(parent)
            .await
            .map_err(|e| AppError::Internal(format!("创建存储目录失败: {e}")))?;
    }

    // 先在同一存储卷的会话临时目录中流式拼接，校验完成后再原子 rename 到正式路径。
    // 每次合并拥有独立临时文件；超时接管时旧请求仍可能在运行。
    let merge_tmp =
        storage::tmp_dir(&state.cfg.storage.root, &s.id).join(format!("{stored_name}.tmp"));
    storage::ensure_within_root(&state.cfg.storage.root, &merge_tmp)?;
    let _ = tokio::fs::remove_file(&merge_tmp).await;
    let mut sha256_hasher = Sha256::new();
    let mut md5_hasher = Md5::new();
    let mut written = 0u64;
    {
        use tokio::io::{AsyncReadExt, AsyncWriteExt};
        let mut output = tokio::fs::File::create(&merge_tmp)
            .await
            .map_err(|e| AppError::Internal(format!("创建合并临时文件失败: {e}")))?;
        let mut buffer = vec![0u8; 64 * 1024];
        for i in 0..s.total_chunks {
            let mut input =
                tokio::fs::File::open(storage::chunk_path(&state.cfg.storage.root, &s.id, i))
                    .await
                    .map_err(|e| AppError::Internal(format!("读取分片 {i} 失败: {e}")))?;
            loop {
                let count = input
                    .read(&mut buffer)
                    .await
                    .map_err(|e| AppError::Internal(format!("读取分片 {i} 失败: {e}")))?;
                if count == 0 {
                    break;
                }
                sha256_hasher.update(&buffer[..count]);
                md5_hasher.update(&buffer[..count]);
                output
                    .write_all(&buffer[..count])
                    .await
                    .map_err(|e| AppError::Internal(format!("写入合并临时文件失败: {e}")))?;
                written += count as u64;
            }
        }
        output
            .flush()
            .await
            .map_err(|e| AppError::Internal(format!("刷新合并文件失败: {e}")))?;
        output
            .sync_all()
            .await
            .map_err(|e| AppError::Internal(format!("同步合并文件失败: {e}")))?;
    }
    if written != s.file_size {
        let _ = tokio::fs::remove_file(&merge_tmp).await;
        return Err(AppError::BadRequest(format!(
            "合并文件大小不符：期望 {}，实际 {written}",
            s.file_size
        )));
    }
    let sha256 = hex::encode(sha256_hasher.finalize());
    if let Some(expected) = s.file_md5.as_ref().filter(|v| !v.trim().is_empty()) {
        let actual = hex::encode(md5_hasher.finalize());
        if !actual.eq_ignore_ascii_case(expected.trim()) {
            let _ = tokio::fs::remove_file(&merge_tmp).await;
            return Err(AppError::BadRequest("文件 MD5 校验失败，请重新上传".into()));
        }
    }
    tokio::fs::rename(&merge_tmp, &final_path)
        .await
        .map_err(|e| AppError::Internal(format!("文件原子落盘失败: {e}")))?;

    let direction = if user.user_type == UserType::Supplier {
        FileDirection::S2c
    } else {
        FileDirection::C2s
    };

    let rel_path = final_path
        .strip_prefix(&state.cfg.storage.root)
        .map_err(|_| AppError::Internal("存储路径异常".into()))?
        .to_string_lossy()
        .replace('\\', "/");

    // 文件记录、通知、审计和会话完成状态必须同事务提交；提交前失败才可删除已 rename 的文件。
    let prepared: ApiResult<(DatabaseTransaction, files::Model)> = async {
        let txn = db.begin().await?;
        super::perm::lock_business_state(&txn).await?;
        let (project, round) = lock_writable_target(&txn, user, s).await?;
        let session = upload_sessions::Entity::find_by_id(s.id.clone())
            .lock_exclusive()
            .one(&txn)
            .await?
            .ok_or(AppError::NotFound)?;
        if session.status != UploadStatus::Merging || session.updated_at != s.updated_at {
            return Err(AppError::Conflict("上传会话状态已变化，请重新查询".into()));
        }
        super::perm::recheck_manager(&txn, user.id, "file:upload").await?;
        let model = files::ActiveModel {
            project_id: Set(s.project_id),
            round_id: Set(s.round_id),
            uploader_id: Set(user.id),
            direction: Set(direction),
            original_name: Set(s.file_name.clone()),
            stored_name: Set(stored_name),
            ext: Set(ext),
            size_bytes: Set(s.file_size),
            mime_type: Set(mime_guess::from_path(&s.file_name)
                .first()
                .map(|m| m.to_string())),
            sha256: Set(Some(sha256)),
            storage_path: Set(rel_path),
            status: Set(FileStatus::Available),
            deleted_at: Set(None),
            created_at: Set(now),
            ..Default::default()
        }
        .insert(&txn)
        .await?;
        notify::enqueue_file_upload(
            &txn,
            &project,
            round.round_no,
            &s.file_name,
            user,
            &state.cfg.web.base_url,
        )
        .await?;
        audit::insert(
            &txn,
            Some(user.id),
            Some(user.employee_no.clone()),
            "FILE_UPLOAD",
            Some("file"),
            Some(model.id.to_string()),
            Some(json!({"name": s.file_name, "size": s.file_size, "roundId": s.round_id})),
            None,
        )
        .await?;
        let session_update = upload_sessions::ActiveModel {
            status: Set(UploadStatus::Completed),
            result_file_id: Set(Some(model.id)),
            updated_at: Set(Utc::now()),
            ..Default::default()
        };
        let completed = upload_sessions::Entity::update_many()
            .set(session_update)
            .filter(upload_sessions::Column::Id.eq(&s.id))
            .filter(upload_sessions::Column::Status.eq(UploadStatus::Merging))
            .exec(&txn)
            .await?;
        if completed.rows_affected != 1 {
            return Err(AppError::Conflict("上传会话已被其他请求变更".into()));
        }
        Ok((txn, model))
    }
    .await;
    let (txn, model) = match prepared {
        Ok(prepared) => prepared,
        Err(e) => {
            let _ = tokio::fs::remove_file(&final_path).await;
            return Err(e);
        }
    };
    match commit_upload_transaction(txn).await {
        Ok(()) => Ok(super::file::file_json(&model)),
        Err(commit_error) => {
            let confirmed: Result<Option<files::Model>, sea_orm::DbErr> = async {
                let session = upload_sessions::Entity::find_by_id(s.id.clone())
                    .one(db)
                    .await?;
                let Some(session) = session else {
                    return Ok(None);
                };
                if session.status != UploadStatus::Completed {
                    return Ok(None);
                }
                let Some(file_id) = session.result_file_id else {
                    return Ok(None);
                };
                files::Entity::find_by_id(file_id).one(db).await
            }
            .await;
            match confirmed {
                Ok(Some(committed)) => {
                    tracing::warn!(session_id = %s.id, file_id = committed.id, error = ?commit_error, "文件提交确认丢失，已按完成会话确认成功");
                    Ok(super::file::file_json(&committed))
                }
                Ok(None) => {
                    tracing::warn!(session_id = %s.id, path = %final_path.display(), error = ?commit_error, "文件提交结果未知，保留正式文件待重试或人工核对");
                    Err(commit_error)
                }
                Err(confirm_error) => {
                    tracing::warn!(session_id = %s.id, path = %final_path.display(), error = ?commit_error, confirmation_error = ?confirm_error, "无法确认文件提交结果，保留正式文件待重试或人工核对");
                    Err(commit_error)
                }
            }
        }
    }
}

/// 过期会话清理（由定时任务调用）
pub async fn gc_expired(state: &AppState) {
    let db = &state.db;
    let expired = upload_sessions::Entity::find()
        .filter(upload_sessions::Column::Status.eq(UploadStatus::Uploading))
        .filter(upload_sessions::Column::ExpiresAt.lt(Utc::now()))
        .all(db)
        .await;
    let Ok(expired) = expired else { return };
    for s in expired {
        let now = Utc::now();
        let expired_update = upload_sessions::ActiveModel {
            status: Set(UploadStatus::Expired),
            updated_at: Set(now),
            ..Default::default()
        };
        let claimed = upload_sessions::Entity::update_many()
            .set(expired_update)
            .filter(upload_sessions::Column::Id.eq(&s.id))
            .filter(upload_sessions::Column::Status.eq(UploadStatus::Uploading))
            .filter(upload_sessions::Column::ExpiresAt.lt(now))
            .exec(db)
            .await;
        match claimed {
            Ok(result) if result.rows_affected == 1 => {
                if let Err(e) = tokio::fs::remove_dir_all(&s.temp_dir).await {
                    if e.kind() != std::io::ErrorKind::NotFound {
                        tracing::warn!(error = ?e, session_id = %s.id, "清理过期上传目录失败");
                    }
                }
            }
            Ok(_) => {}
            Err(e) => {
                tracing::warn!(error = ?e, session_id = %s.id, "过期上传会话认领失败");
            }
        }
    }
}

#[cfg(test)]
mod regression_tests {
    use super::*;

    async fn await_waiting_on(db: &DatabaseConnection, blocker: u64) {
        use sea_orm::{ConnectionTrait, DbBackend, Statement};
        tokio::time::timeout(std::time::Duration::from_secs(5), async {
            loop {
                let row = db.query_one(Statement::from_string(DbBackend::MySql, format!("SELECT COUNT(*) AS n FROM information_schema.INNODB_LOCK_WAITS w JOIN information_schema.INNODB_TRX b ON b.trx_id=w.blocking_trx_id WHERE b.trx_mysql_thread_id={blocker}"))).await.unwrap().unwrap();
                if row.try_get::<i64>("", "n").unwrap() > 0 {
                    break;
                }
                tokio::time::sleep(std::time::Duration::from_millis(100)).await;
            }
        })
        .await
        .expect("upload operation never reached the owned lock barrier");
    }

    #[tokio::test]
    #[ignore = "isolated MySQL required"]
    async fn init_commit_confirmation_loss_keeps_committed_session_directory() {
        let f = crate::regression::Fixture::new().await;
        let result = LOSE_UPLOAD_COMMIT_CONFIRMATION
            .scope(
                true,
                init(
                    &f.state,
                    &f.member,
                    &InitReq {
                        project_id: f.project_id,
                        round_id: f.round_id,
                        file_name: "init-commit-confirmation.pdf".into(),
                        file_size: 4,
                        file_md5: None,
                    },
                ),
            )
            .await
            .unwrap();
        let sid = result["sessionId"].as_str().unwrap();
        let session = upload_sessions::Entity::find_by_id(sid)
            .one(&f.state.db)
            .await
            .unwrap()
            .unwrap();
        assert_eq!(session.status, UploadStatus::Uploading);
        assert!(
            storage::tmp_dir(&f.state.cfg.storage.root, sid).is_dir(),
            "a committed upload session must keep its temporary directory"
        );
    }

    #[tokio::test]
    #[ignore = "isolated MySQL required"]
    async fn merge_commit_confirmation_loss_returns_the_single_committed_file() {
        use sea_orm::PaginatorTrait;

        let f = crate::regression::Fixture::new().await;
        let sid = f.init("merge-commit-confirmation.pdf").await;
        put_chunk(&f.state, &f.member, &sid, 0, b"test")
            .await
            .unwrap();

        let merged = LOSE_UPLOAD_COMMIT_CONFIRMATION
            .scope(true, merge(&f.state, &f.member, &sid))
            .await
            .unwrap();
        let session = upload_sessions::Entity::find_by_id(sid.clone())
            .one(&f.state.db)
            .await
            .unwrap()
            .unwrap();
        assert_eq!(session.status, UploadStatus::Completed);
        let file_id = session.result_file_id.unwrap();
        assert_eq!(merged["id"], file_id);
        let file = files::Entity::find_by_id(file_id)
            .one(&f.state.db)
            .await
            .unwrap()
            .unwrap();
        assert!(
            std::path::Path::new(&f.state.cfg.storage.root)
                .join(&file.storage_path)
                .is_file(),
            "a confirmed committed file must not be removed"
        );
        assert_eq!(merge(&f.state, &f.member, &sid).await.unwrap(), merged);
        assert_eq!(
            files::Entity::find()
                .filter(files::Column::ProjectId.eq(f.project_id))
                .count(&f.state.db)
                .await
                .unwrap(),
            1
        );
    }

    #[tokio::test]
    #[ignore = "isolated MySQL required"]
    async fn chunk_waiting_on_locks_rechecks_session_expiry_before_writing() {
        use sea_orm::{ConnectionTrait, DbBackend, Statement};

        let f = crate::regression::Fixture::new().await;
        let sid = f.init("expires-while-waiting.pdf").await;
        let txn = f.state.db.begin().await.unwrap();
        crate::service::perm::lock_management_state(&txn)
            .await
            .unwrap();
        projects::Entity::find_by_id(f.project_id)
            .lock_exclusive()
            .one(&txn)
            .await
            .unwrap()
            .unwrap();
        rounds::Entity::find_by_id(f.round_id)
            .lock_exclusive()
            .one(&txn)
            .await
            .unwrap()
            .unwrap();
        let locked = upload_sessions::Entity::find_by_id(sid.clone())
            .lock_exclusive()
            .one(&txn)
            .await
            .unwrap()
            .unwrap();
        let blocker = txn
            .query_one(Statement::from_string(
                DbBackend::MySql,
                "SELECT CONNECTION_ID() AS id".to_owned(),
            ))
            .await
            .unwrap()
            .unwrap()
            .try_get("", "id")
            .unwrap();

        let (state, actor, pending_sid) = (f.state.clone(), f.member.clone(), sid.clone());
        let pending =
            tokio::spawn(async move { put_chunk(&state, &actor, &pending_sid, 0, b"test").await });
        await_waiting_on(&f.state.db, blocker).await;

        let mut expired: upload_sessions::ActiveModel = locked.into();
        expired.expires_at = Set(Utc::now() - Duration::seconds(1));
        expired.update(&txn).await.unwrap();
        txn.commit().await.unwrap();

        assert!(matches!(pending.await.unwrap(), Err(AppError::Conflict(_))));
        assert!(
            !storage::chunk_path(&f.state.cfg.storage.root, &sid, 0).exists(),
            "an expired session must not accept a chunk after lock wait"
        );
    }

    #[tokio::test]
    #[ignore = "isolated MySQL required"]
    async fn expired_gc_cannot_delete_or_expire_a_session_that_started_merging() {
        use sea_orm::{ConnectionTrait, DbBackend, Statement};

        let f = crate::regression::Fixture::new().await;
        let sid = f.init("gc-merge-race.pdf").await;
        put_chunk(&f.state, &f.member, &sid, 0, b"test")
            .await
            .unwrap();
        let session = load_session(&f.state.db, &sid).await.unwrap();
        let mut expired: upload_sessions::ActiveModel = session.into();
        expired.expires_at = Set(Utc::now() - Duration::seconds(1));
        expired.update(&f.state.db).await.unwrap();

        let txn = f.state.db.begin().await.unwrap();
        let locked = upload_sessions::Entity::find_by_id(sid.clone())
            .lock_exclusive()
            .one(&txn)
            .await
            .unwrap()
            .unwrap();
        let blocker = txn
            .query_one(Statement::from_string(
                DbBackend::MySql,
                "SELECT CONNECTION_ID() AS id".to_owned(),
            ))
            .await
            .unwrap()
            .unwrap()
            .try_get("", "id")
            .unwrap();
        let state = f.state.clone();
        let gc = tokio::spawn(async move { gc_expired(&state).await });
        await_waiting_on(&f.state.db, blocker).await;

        let mut merging: upload_sessions::ActiveModel = locked.into();
        merging.status = Set(UploadStatus::Merging);
        merging.updated_at = Set(Utc::now());
        merging.update(&txn).await.unwrap();
        txn.commit().await.unwrap();
        gc.await.unwrap();

        let current = upload_sessions::Entity::find_by_id(sid.clone())
            .one(&f.state.db)
            .await
            .unwrap()
            .unwrap();
        assert_eq!(current.status, UploadStatus::Merging);
        assert!(
            storage::chunk_path(&f.state.cfg.storage.root, &sid, 0).exists(),
            "GC must not delete chunks after another transaction starts merging"
        );
    }

    #[tokio::test]
    #[ignore = "isolated MySQL required"]
    async fn takeover_merge_does_not_touch_previous_attempt_temp_file() {
        let f = crate::regression::Fixture::new().await;
        let sid = f.init("takeover.pdf").await;
        put_chunk(&f.state, &f.member, &sid, 0, b"test")
            .await
            .unwrap();
        let session = load_session(&f.state.db, &sid).await.unwrap();
        let mut active: upload_sessions::ActiveModel = session.clone().into();
        active.status = Set(UploadStatus::Merging);
        active.update(&f.state.db).await.unwrap();
        // MySQL may refresh updated_at when status changes; use the persisted lease.
        let session = load_session(&f.state.db, &sid).await.unwrap();
        let old_temp = storage::tmp_dir(&f.state.cfg.storage.root, &sid).join("merged.tmp");
        tokio::fs::write(&old_temp, b"previous-owner-in-progress")
            .await
            .unwrap();
        do_merge(&f.state, &f.member, &session).await.unwrap();
        assert_eq!(
            tokio::fs::read(&old_temp).await.unwrap(),
            b"previous-owner-in-progress",
            "接管合并不能删除或覆盖旧请求正在使用的临时文件"
        );
    }

    #[tokio::test]
    #[ignore = "isolated MySQL required"]
    async fn final_merge_rechecks_round_closed_after_initial_validation() {
        let f = crate::regression::Fixture::new().await;
        let sid = f.init("closed.pdf").await;
        put_chunk(&f.state, &f.member, &sid, 0, b"test")
            .await
            .unwrap();
        let session = load_session(&f.state.db, &sid).await.unwrap();
        let mut active: upload_sessions::ActiveModel = session.clone().into();
        active.status = Set(UploadStatus::Merging);
        active.update(&f.state.db).await.unwrap();
        crate::service::round::confirm(&f.state.db, &f.state.cfg, &f.admin, f.round_id)
            .await
            .unwrap();
        assert!(
            matches!(
                do_merge(&f.state, &f.member, &session).await,
                Err(AppError::Conflict(_))
            ),
            "合并最终提交不得向已关闭轮次插入文件"
        );
    }
}
