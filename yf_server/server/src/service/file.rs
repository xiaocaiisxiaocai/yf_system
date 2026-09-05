//! 文件：列表 / 下载 / 内联预览 / 删除 / 批量打包（zip）
use axum::body::Body;
use axum::http::{header, HeaderMap, HeaderValue, StatusCode};
use axum::response::{IntoResponse, Response};
use sea_orm::{
    ActiveModelTrait, ColumnTrait, Condition, DatabaseConnection, EntityTrait, PaginatorTrait,
    QueryFilter, QueryOrder, Set, TransactionTrait,
};
use serde::Deserialize;
use serde_json::{json, Value};

use crate::dto::PageResp;
use crate::entity::enums::FileStatus;
use crate::entity::{files, rounds, users};
use crate::error::{ApiResult, AppError};
use crate::middleware::auth::CurrentUser;
use crate::state::AppState;

use super::{audit, scope};

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct FileListQuery {
    #[serde(default = "crate::dto::default_page")]
    pub page: u64,
    #[serde(default = "crate::dto::default_page_size")]
    pub page_size: u64,
    pub round_id: Option<u64>,
    pub direction: Option<String>,
    pub keyword: Option<String>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct BatchDownloadReq {
    pub ids: Vec<u64>,
}

pub fn file_json(f: &files::Model) -> Value {
    json!({
        "id": f.id, "projectId": f.project_id, "roundId": f.round_id,
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
    if let Some(rid) = q.round_id {
        cond = cond.add(files::Column::RoundId.eq(rid));
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
    let can_delete = scope::is_system_admin(db, user.id).await?;
    let uploader_map: std::collections::HashMap<u64, String> = users::Entity::find()
        .filter(users::Column::Id.is_in(items.iter().map(|f| f.uploader_id).collect::<Vec<_>>()))
        .all(db)
        .await?
        .into_iter()
        .map(|u| (u.id, u.real_name))
        .collect();
    let round_map: std::collections::HashMap<u64, i32> = rounds::Entity::find()
        .filter(rounds::Column::Id.is_in(items.iter().map(|f| f.round_id).collect::<Vec<_>>()))
        .all(db)
        .await?
        .into_iter()
        .map(|r| (r.id, r.round_no))
        .collect();
    let mut list = Vec::with_capacity(items.len());
    for f in &items {
        let mut value = file_json(f);
        value["uploaderName"] = json!(uploader_map.get(&f.uploader_id));
        value["roundNo"] = json!(round_map.get(&f.round_id));
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

    let abs = std::path::Path::new(&state.cfg.storage.root).join(&f.storage_path);
    crate::storage::ensure_within_root(&state.cfg.storage.root, &abs)?;
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
        HeaderValue::from_str(&f.size_bytes.to_string()).unwrap_or(HeaderValue::from_static("0")),
    );

    if !inline {
        audit::log(
            db,
            Some(user.id),
            Some(user.username.clone()),
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
    scope::ensure_project_access(db, user, f.project_id).await?;
    if !scope::is_system_admin(db, user.id).await? {
        return Err(AppError::Forbidden);
    }
    if f.status != FileStatus::Available {
        return Err(AppError::NotFound);
    }
    let txn = db.begin().await?;
    let mut am: files::ActiveModel = f.clone().into();
    am.status = Set(FileStatus::Deleted);
    am.deleted_at = Set(Some(chrono::Utc::now()));
    am.update(&txn).await?;
    audit::insert(
        &txn,
        Some(user.id),
        Some(user.username.clone()),
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
    let mut files = Vec::new();
    for id in &req.ids {
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

    let root = state.cfg.storage.root.clone();
    let zip_name = format!("yf_files_{}.zip", uuid::Uuid::new_v4());
    let zip_path = std::path::Path::new(&root).join("tmp").join(&zip_name);
    tokio::fs::create_dir_all(zip_path.parent().unwrap())
        .await
        .ok();

    let files_for_zip: Vec<(std::path::PathBuf, String)> = files
        .iter()
        .map(|f| {
            (
                std::path::Path::new(&root).join(&f.storage_path),
                f.original_name.clone(),
            )
        })
        .collect();
    let zip_path_clone = zip_path.clone();
    tokio::task::spawn_blocking(move || -> ApiResult<()> {
        let file = std::fs::File::create(&zip_path_clone)
            .map_err(|e| AppError::Internal(format!("创建压缩包失败: {e}")))?;
        let mut zip = zip::ZipWriter::new(file);
        let opts = zip::write::SimpleFileOptions::default();
        let mut used_names = std::collections::HashSet::new();
        for (path, name) in &files_for_zip {
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
            // 流式拷贝，避免大文件整读进内存
            let mut src = std::fs::File::open(path)
                .map_err(|e| AppError::Internal(format!("读取文件失败: {e}")))?;
            std::io::copy(&mut src, &mut zip)
                .map_err(|e| AppError::Internal(format!("压缩写入失败: {e}")))?;
        }
        zip.finish()
            .map_err(|e| AppError::Internal(format!("压缩完成失败: {e}")))?;
        Ok(())
    })
    .await
    .map_err(|e| AppError::Internal(format!("压缩任务失败: {e}")))??;

    let meta = tokio::fs::metadata(&zip_path)
        .await
        .map_err(|e| AppError::Internal(e.to_string()))?;
    let file = tokio::fs::File::open(&zip_path)
        .await
        .map_err(|e| AppError::Internal(e.to_string()))?;
    let stream = tokio_util::io::ReaderStream::new(file);

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

    audit::log(
        db,
        Some(user.id),
        Some(user.username.clone()),
        "FILE_BATCH_DOWNLOAD",
        Some("file"),
        None,
        Some(json!({"ids": req.ids})),
        None,
    )
    .await;
    // 临时 zip 由后续清理任务/人工清理（tmp 目录），此处不阻塞响应
    Ok((StatusCode::OK, headers, Body::from_stream(stream)).into_response())
}
