//! 文件：列表 / 下载 / 内联预览 / 删除 / 批量打包（zip）
use axum::body::Body;
use axum::http::{header, HeaderMap, HeaderValue, StatusCode};
use axum::response::{IntoResponse, Response};
use sea_orm::{
    ActiveModelTrait, ColumnTrait, Condition, DatabaseConnection, EntityTrait, PaginatorTrait,
    QueryFilter, QueryOrder, QuerySelect, Set, TransactionTrait,
};
use serde::Deserialize;
use serde_json::{json, Value};
use std::collections::{HashMap, HashSet};
use std::path::{Path, PathBuf};
use std::pin::Pin;
use std::sync::{Arc, Mutex, OnceLock};
use std::task::{Context, Poll};
use tokio::io::{AsyncRead, ReadBuf};

use crate::dto::PageResp;
use crate::entity::enums::{FileStatus, ProjectStatus};
use crate::entity::{files, projects, users};
use crate::error::{ApiResult, AppError};
use crate::middleware::auth::CurrentUser;
use crate::state::AppState;

use super::{audit, scope};

pub(crate) const PDF_PREVIEW_MAX_BYTES: u64 = 50 * 1024 * 1024;
pub(crate) const BATCH_INPUT_MAX_BYTES: u64 = 256 * 1024 * 1024;
const BATCH_ZIP_OVERHEAD_BYTES: u64 = 2 * 1024 * 1024;
const BATCH_GLOBAL_MAX_JOBS: usize = 2;
const BATCH_TEMP_BUDGET_BYTES: u64 =
    BATCH_GLOBAL_MAX_JOBS as u64 * (BATCH_INPUT_MAX_BYTES + BATCH_ZIP_OVERHEAD_BYTES);

#[derive(Default)]
struct BatchResourceState {
    active_jobs: usize,
    active_users: HashMap<u64, usize>,
    reserved_bytes: u64,
}

pub(crate) struct BatchLimiter {
    state: Arc<Mutex<BatchResourceState>>,
    max_jobs: usize,
    max_reserved_bytes: u64,
}

impl BatchLimiter {
    pub(crate) fn new(max_jobs: usize, max_reserved_bytes: u64) -> Self {
        Self {
            state: Arc::new(Mutex::new(BatchResourceState::default())),
            max_jobs,
            max_reserved_bytes,
        }
    }

    pub(crate) fn try_acquire(&self, user_id: u64, reserved_bytes: u64) -> ApiResult<BatchPermit> {
        let mut state = self
            .state
            .lock()
            .map_err(|_| AppError::Internal("批量下载资源状态不可用".into()))?;
        if state.active_users.contains_key(&user_id) {
            return Err(AppError::Conflict(
                "当前账号已有批量下载正在处理，请完成后重试".into(),
            ));
        }
        if state.active_jobs >= self.max_jobs {
            return Err(AppError::Conflict("批量下载任务繁忙，请稍后重试".into()));
        }
        let next_reserved = state
            .reserved_bytes
            .checked_add(reserved_bytes)
            .ok_or_else(|| AppError::BadRequest("批量下载文件总大小超出限制".into()))?;
        if next_reserved > self.max_reserved_bytes {
            return Err(AppError::Conflict(
                "批量下载临时空间繁忙，请稍后重试".into(),
            ));
        }
        state.active_jobs += 1;
        state.active_users.insert(user_id, 1);
        state.reserved_bytes = next_reserved;
        Ok(BatchPermit {
            state: Arc::clone(&self.state),
            user_id,
            reserved_bytes,
        })
    }
}

pub(crate) struct BatchPermit {
    state: Arc<Mutex<BatchResourceState>>,
    user_id: u64,
    reserved_bytes: u64,
}

impl Drop for BatchPermit {
    fn drop(&mut self) {
        let Ok(mut state) = self.state.lock() else {
            return;
        };
        state.active_jobs = state.active_jobs.saturating_sub(1);
        state.active_users.remove(&self.user_id);
        state.reserved_bytes = state.reserved_bytes.saturating_sub(self.reserved_bytes);
    }
}

fn batch_limiter() -> &'static BatchLimiter {
    static LIMITER: OnceLock<BatchLimiter> = OnceLock::new();
    LIMITER.get_or_init(|| BatchLimiter::new(BATCH_GLOBAL_MAX_JOBS, BATCH_TEMP_BUDGET_BYTES))
}

pub(crate) fn unique_batch_ids(ids: &[u64]) -> Vec<u64> {
    let mut seen = HashSet::with_capacity(ids.len());
    ids.iter().copied().filter(|id| seen.insert(*id)).collect()
}

pub(crate) fn checked_batch_input_size(sizes: impl IntoIterator<Item = u64>) -> ApiResult<u64> {
    let mut total = 0u64;
    for size in sizes {
        total = total
            .checked_add(size)
            .ok_or_else(|| AppError::BadRequest("批量下载文件总大小超出限制".into()))?;
        if total > BATCH_INPUT_MAX_BYTES {
            return Err(AppError::BadRequest(format!(
                "批量下载文件总大小不能超过 {} MiB",
                BATCH_INPUT_MAX_BYTES / 1024 / 1024
            )));
        }
    }
    Ok(total)
}

pub(crate) fn ensure_pdf_preview_size(ext: &str, size_bytes: u64) -> ApiResult<()> {
    if ext.eq_ignore_ascii_case("pdf") && size_bytes > PDF_PREVIEW_MAX_BYTES {
        return Err(AppError::BadRequest(format!(
            "PDF 超过 {} MiB，不能在线预览，请下载原文件查看",
            PDF_PREVIEW_MAX_BYTES / 1024 / 1024
        )));
    }
    Ok(())
}

struct ArchiveArtifact {
    path: PathBuf,
    permit: Option<BatchPermit>,
    armed: bool,
}

impl ArchiveArtifact {
    fn new(path: PathBuf, permit: BatchPermit) -> Self {
        Self {
            path,
            permit: Some(permit),
            armed: true,
        }
    }

    fn into_reader(mut self, file: tokio::fs::File) -> ArchiveReader {
        self.armed = false;
        ArchiveReader {
            file: Some(file),
            path: self.path.clone(),
            permit: self.permit.take(),
        }
    }
}

impl Drop for ArchiveArtifact {
    fn drop(&mut self) {
        if self.armed {
            remove_temp_zip(&self.path);
        }
    }
}

struct ArchiveReader {
    file: Option<tokio::fs::File>,
    path: PathBuf,
    permit: Option<BatchPermit>,
}

impl ArchiveReader {
    fn cleanup(&mut self) {
        if self.file.take().is_some() {
            remove_temp_zip(&self.path);
        }
        self.permit.take();
    }
}

impl AsyncRead for ArchiveReader {
    fn poll_read(
        self: Pin<&mut Self>,
        cx: &mut Context<'_>,
        buf: &mut ReadBuf<'_>,
    ) -> Poll<std::io::Result<()>> {
        let this = self.get_mut();
        let Some(file) = this.file.as_mut() else {
            return Poll::Ready(Ok(()));
        };
        let before = buf.filled().len();
        match Pin::new(file).poll_read(cx, buf) {
            Poll::Ready(Ok(())) if buf.filled().len() == before => {
                this.cleanup();
                Poll::Ready(Ok(()))
            }
            other => other,
        }
    }
}

impl Drop for ArchiveReader {
    fn drop(&mut self) {
        self.cleanup();
    }
}

fn remove_temp_zip(path: &Path) {
    if let Err(error) = std::fs::remove_file(path) {
        if error.kind() != std::io::ErrorKind::NotFound {
            tracing::warn!(error = ?error, path = %path.display(), "清理批量下载临时文件失败");
        }
    }
}

fn build_archive(
    path: PathBuf,
    files: Vec<(PathBuf, String)>,
    permit: BatchPermit,
) -> ApiResult<ArchiveArtifact> {
    let artifact = ArchiveArtifact::new(path, permit);
    let file = std::fs::File::create(&artifact.path)
        .map_err(|e| AppError::Internal(format!("创建压缩包失败: {e}")))?;
    let mut zip = zip::ZipWriter::new(file);
    let opts = zip::write::SimpleFileOptions::default();
    let mut used_names = HashSet::new();
    let mut copied_total = 0u64;
    for (source_path, name) in &files {
        let base = name
            .rsplit(['/', '\\'])
            .next()
            .filter(|n| !n.is_empty())
            .unwrap_or("file");
        let (stem, extension) = base
            .rsplit_once('.')
            .map(|(s, e)| (s, format!(".{e}")))
            .unwrap_or((base, String::new()));
        let mut entry_name = base.to_string();
        let mut suffix = 2;
        while !used_names.insert(entry_name.to_lowercase()) {
            entry_name = format!("{stem} ({suffix}){extension}");
            suffix += 1;
        }
        zip.start_file(entry_name, opts)
            .map_err(|e| AppError::Internal(format!("压缩失败: {e}")))?;
        let src = std::fs::File::open(source_path)
            .map_err(|e| AppError::Internal(format!("读取文件失败: {e}")))?;
        let remaining = BATCH_INPUT_MAX_BYTES.saturating_sub(copied_total);
        let copied = std::io::copy(&mut std::io::Read::take(src, remaining + 1), &mut zip)
            .map_err(|e| AppError::Internal(format!("压缩写入失败: {e}")))?;
        copied_total = copied_total.saturating_add(copied);
        if copied_total > BATCH_INPUT_MAX_BYTES {
            return Err(AppError::BadRequest(format!(
                "批量下载文件总大小不能超过 {} MiB",
                BATCH_INPUT_MAX_BYTES / 1024 / 1024
            )));
        }
    }
    zip.finish()
        .map_err(|e| AppError::Internal(format!("压缩完成失败: {e}")))?;
    Ok(artifact)
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct FileListQuery {
    #[serde(default = "crate::dto::default_page")]
    pub page: u64,
    #[serde(default = "crate::dto::default_page_size")]
    pub page_size: u64,
    pub direction: Option<String>,
    pub keyword: Option<String>,
    /// 精确定位项目动态中的文件，仍叠加项目范围及可用状态。
    pub target_id: Option<u64>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct BatchDownloadReq {
    pub ids: Vec<u64>,
}

pub fn file_json(f: &files::Model) -> Value {
    json!({
        "id": f.id, "projectId": f.project_id,
        "uploaderId": f.uploader_id,
        "direction": if f.direction == crate::entity::enums::FileDirection::C2s { "C2S" } else { "S2C" },
        "originalName": f.original_name, "ext": f.ext,
        "sizeBytes": f.size_bytes, "mimeType": f.mime_type,
        "sha256": f.sha256, "createdAt": f.created_at,
    })
}

pub async fn list(
    db: &DatabaseConnection,
    user: &CurrentUser,
    project_id: u64,
    q: &FileListQuery,
) -> ApiResult<PageResp<Value>> {
    scope::ensure_project_access(db, user, project_id).await?;
    let (page, size) = crate::dto::clamp_page(q.page, q.page_size);
    let mut cond = Condition::all()
        .add(files::Column::ProjectId.eq(project_id))
        .add(files::Column::Status.eq(FileStatus::Available));
    if let Some(id) = q.target_id {
        cond = cond.add(files::Column::Id.eq(id));
    }
    if let Some(d) = &q.direction {
        cond = cond.add(files::Column::Direction.eq(d.as_str()));
    }
    if let Some(kw) = q.keyword.as_ref().filter(|k| !k.trim().is_empty()) {
        cond = cond.add(files::Column::OriginalName.contains(kw.trim()));
    }
    let paginator = files::Entity::find()
        .filter(cond)
        .order_by_desc(files::Column::Id)
        .paginate(db, size);
    let total = paginator.num_items().await?;
    let items = paginator.fetch_page(page - 1).await?;
    let can_delete = super::perm::permission_codes(db, user.id)
        .await?
        .iter()
        .any(|code| code == "file:delete");
    let uploader_map: std::collections::HashMap<u64, String> = users::Entity::find()
        .filter(users::Column::Id.is_in(items.iter().map(|f| f.uploader_id).collect::<Vec<_>>()))
        .all(db)
        .await?
        .into_iter()
        .map(|u| (u.id, u.real_name))
        .collect();
    let mut list = Vec::with_capacity(items.len());
    for f in &items {
        let mut value = file_json(f);
        value["uploaderName"] = json!(uploader_map.get(&f.uploader_id));
        value["canDelete"] = json!(can_delete);
        list.push(value);
    }
    Ok(PageResp::new(list, total, page, size))
}

/// 流式返回文件（download=attachment / content=inline）
pub async fn stream_file(
    state: &AppState,
    user: &CurrentUser,
    id: u64,
    inline: bool,
) -> ApiResult<Response> {
    let db = &state.db;
    let f = files::Entity::find_by_id(id)
        .one(db)
        .await?
        .ok_or(AppError::NotFound)?;
    if f.status != FileStatus::Available {
        return Err(AppError::NotFound);
    }
    scope::ensure_project_access(db, user, f.project_id).await?;

    let stored_path = std::path::Path::new(&state.cfg.storage.root).join(&f.storage_path);
    let abs =
        crate::storage::canonical_existing_path(&state.cfg.storage.root, &stored_path).await?;
    let metadata = tokio::fs::metadata(&abs)
        .await
        .map_err(|_| AppError::NotFound)?;
    if inline {
        ensure_pdf_preview_size(&f.ext, f.size_bytes.max(metadata.len()))?;
    }
    let file = tokio::fs::File::open(&abs)
        .await
        .map_err(|_| AppError::NotFound)?;
    let stream = tokio_util::io::ReaderStream::new(file);

    let encoded = urlencoding::encode(&f.original_name);
    let disposition = format!(
        "{}; filename*=UTF-8''{}",
        if inline { "inline" } else { "attachment" },
        encoded
    );
    let mime = f
        .mime_type
        .clone()
        .unwrap_or_else(|| "application/octet-stream".to_string());

    let mut headers = HeaderMap::new();
    headers.insert(
        header::CONTENT_TYPE,
        HeaderValue::from_str(&mime)
            .unwrap_or(HeaderValue::from_static("application/octet-stream")),
    );
    headers.insert(
        header::CONTENT_DISPOSITION,
        HeaderValue::from_str(&disposition)
            .map_err(|_| AppError::Internal("文件名编码失败".into()))?,
    );
    headers.insert(
        header::CONTENT_LENGTH,
        HeaderValue::from_str(&metadata.len().to_string()).unwrap_or(HeaderValue::from_static("0")),
    );
    headers.insert(
        header::CACHE_CONTROL,
        HeaderValue::from_static("private, no-store"),
    );

    if !inline {
        audit::log(
            db,
            Some(user.id),
            Some(user.employee_no.clone()),
            "FILE_DOWNLOAD",
            Some("file"),
            Some(id.to_string()),
            Some(json!({"name": f.original_name})),
            None,
        )
        .await;
    }
    Ok((StatusCode::OK, headers, Body::from_stream(stream)).into_response())
}

pub async fn delete(state: &AppState, user: &CurrentUser, id: u64) -> ApiResult<()> {
    let db = &state.db;
    let f = files::Entity::find_by_id(id)
        .one(db)
        .await?
        .ok_or(AppError::NotFound)?;
    let txn = db.begin().await?;
    super::perm::lock_business_state(&txn).await?;
    let project = projects::Entity::find_by_id(f.project_id)
        .lock_exclusive()
        .one(&txn)
        .await?
        .ok_or(AppError::NotFound)?;
    let f = files::Entity::find_by_id(id)
        .lock_exclusive()
        .one(&txn)
        .await?
        .ok_or(AppError::NotFound)?;
    super::perm::recheck_manager(&txn, user.id, "file:delete").await?;
    scope::ensure_project_access(&txn, user, f.project_id).await?;
    if project.status != ProjectStatus::InProgress {
        return Err(AppError::Conflict("项目当前不可删除文件".into()));
    }
    if f.status != FileStatus::Available {
        return Err(AppError::NotFound);
    }
    let mut am: files::ActiveModel = f.clone().into();
    am.status = Set(FileStatus::Deleted);
    am.deleted_at = Set(Some(chrono::Utc::now()));
    am.update(&txn).await?;
    audit::insert(
        &txn,
        Some(user.id),
        Some(user.employee_no.clone()),
        "FILE_DELETE",
        Some("file"),
        Some(id.to_string()),
        Some(json!({"name": f.original_name})),
        None,
    )
    .await?;
    txn.commit().await?;
    Ok(())
}

/// 批量打包下载：在临时目录生成 zip 后流式返回
pub async fn batch_download(
    state: &AppState,
    user: &CurrentUser,
    req: &BatchDownloadReq,
) -> ApiResult<Response> {
    let db = &state.db;
    if req.ids.is_empty() || req.ids.len() > 100 {
        return Err(AppError::BadRequest("批量下载数量需为 1~100".into()));
    }
    let unique_ids = unique_batch_ids(&req.ids);
    let mut files = Vec::with_capacity(unique_ids.len());
    for id in &unique_ids {
        let f = files::Entity::find_by_id(*id)
            .one(db)
            .await?
            .ok_or(AppError::NotFound)?;
        if f.status != FileStatus::Available {
            return Err(AppError::BadRequest(format!(
                "文件 {} 不可用",
                f.original_name
            )));
        }
        scope::ensure_project_access(db, user, f.project_id).await?;
        files.push(f);
    }

    let mut files_for_zip = Vec::with_capacity(files.len());
    let root = state.cfg.storage.root.clone();
    let mut source_sizes = Vec::with_capacity(files.len());
    for file in &files {
        let stored_path = std::path::Path::new(&root).join(&file.storage_path);
        let canonical_path = crate::storage::canonical_existing_path(&root, &stored_path).await?;
        let metadata = tokio::fs::metadata(&canonical_path)
            .await
            .map_err(|_| AppError::NotFound)?;
        if !metadata.is_file() {
            return Err(AppError::NotFound);
        }
        source_sizes.push(metadata.len());
        files_for_zip.push((canonical_path, file.original_name.clone()));
    }
    let input_bytes = checked_batch_input_size(source_sizes)?;
    let reserved_bytes = input_bytes
        .checked_add(BATCH_ZIP_OVERHEAD_BYTES)
        .ok_or_else(|| AppError::BadRequest("批量下载文件总大小超出限制".into()))?;
    let permit = batch_limiter().try_acquire(user.id, reserved_bytes)?;

    let zip_name = format!("yf_files_{}.zip", uuid::Uuid::new_v4());
    let zip_path = Path::new(&root).join("tmp").join(&zip_name);
    tokio::fs::create_dir_all(
        zip_path
            .parent()
            .ok_or_else(|| AppError::Internal("临时压缩目录无效".into()))?,
    )
    .await
    .map_err(|e| AppError::Internal(format!("创建临时压缩目录失败: {e}")))?;

    let artifact =
        tokio::task::spawn_blocking(move || build_archive(zip_path, files_for_zip, permit))
            .await
            .map_err(|e| AppError::Internal(format!("压缩任务失败: {e}")))??;

    let meta = tokio::fs::metadata(&artifact.path)
        .await
        .map_err(|e| AppError::Internal(e.to_string()))?;
    let file = tokio::fs::File::open(&artifact.path)
        .await
        .map_err(|e| AppError::Internal(e.to_string()))?;
    let stream = tokio_util::io::ReaderStream::new(artifact.into_reader(file));

    let mut headers = HeaderMap::new();
    headers.insert(
        header::CONTENT_TYPE,
        HeaderValue::from_static("application/zip"),
    );
    headers.insert(
        header::CONTENT_DISPOSITION,
        HeaderValue::from_str(&format!(
            "attachment; filename*=UTF-8''{}",
            urlencoding::encode(&zip_name)
        ))
        .unwrap(),
    );
    headers.insert(
        header::CONTENT_LENGTH,
        HeaderValue::from_str(&meta.len().to_string()).unwrap(),
    );
    headers.insert(
        header::CACHE_CONTROL,
        HeaderValue::from_static("private, no-store"),
    );

    audit::log(
        db,
        Some(user.id),
        Some(user.employee_no.clone()),
        "FILE_BATCH_DOWNLOAD",
        Some("file"),
        None,
        Some(json!({"ids": unique_ids, "inputBytes": input_bytes})),
        None,
    )
    .await;
    // ArchiveReader 在读完、客户端取消或响应构造失败时关闭文件并删除临时 ZIP。
    Ok((StatusCode::OK, headers, Body::from_stream(stream)).into_response())
}

#[cfg(test)]
pub(crate) async fn archive_body_for_test(path: PathBuf) -> Body {
    let file = tokio::fs::File::open(&path).await.unwrap();
    let reader = ArchiveReader {
        file: Some(file),
        path,
        permit: None,
    };
    Body::from_stream(tokio_util::io::ReaderStream::new(reader))
}

#[cfg(test)]
pub(crate) fn failed_archive_for_test(path: PathBuf, missing_source: PathBuf) -> ApiResult<()> {
    let limiter = BatchLimiter::new(1, BATCH_TEMP_BUDGET_BYTES);
    let permit = limiter.try_acquire(1, BATCH_ZIP_OVERHEAD_BYTES)?;
    build_archive(path, vec![(missing_source, "missing.bin".into())], permit).map(drop)
}
