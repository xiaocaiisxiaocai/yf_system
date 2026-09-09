//! 文件与分片上传 handler
use axum::body::Bytes;
use axum::extract::{Path, State};
use axum::response::Response;
use axum::Json;
use axum_extra::extract::Query;
use serde_json::Value;

use crate::error::ApiResult;
use crate::middleware::auth::CurrentUser;
use crate::service;
use crate::state::AppState;

async fn require(s: &AppState, u: &CurrentUser, code: &str) -> ApiResult<()> {
    service::perm::check_perm(&s.db, u.id, code).await
}

// ---------- 分片上传 ----------

pub async fn init_upload(
    State(s): State<AppState>,
    u: CurrentUser,
    Json(req): Json<service::upload::InitReq>,
) -> ApiResult<Json<Value>> {
    require(&s, &u, "file:upload").await?;
    Ok(Json(service::upload::init(&s, &u, &req).await?))
}

pub async fn upload_chunk(
    State(s): State<AppState>,
    u: CurrentUser,
    Path((sid, index)): Path<(String, u32)>,
    body: Bytes,
) -> ApiResult<Json<Value>> {
    require(&s, &u, "file:upload").await?;
    service::upload::put_chunk(&s, &u, &sid, index, &body).await?;
    Ok(Json(serde_json::json!({})))
}

pub async fn get_upload(
    State(s): State<AppState>,
    u: CurrentUser,
    Path(sid): Path<String>,
) -> ApiResult<Json<Value>> {
    require(&s, &u, "file:upload").await?;
    Ok(Json(service::upload::status(&s, &u, &sid).await?))
}

pub async fn merge_upload(
    State(s): State<AppState>,
    u: CurrentUser,
    Path(sid): Path<String>,
) -> ApiResult<Json<Value>> {
    require(&s, &u, "file:upload").await?;
    Ok(Json(service::upload::merge(&s, &u, &sid).await?))
}

pub async fn abort_upload(
    State(s): State<AppState>,
    u: CurrentUser,
    Path(sid): Path<String>,
) -> ApiResult<Json<Value>> {
    require(&s, &u, "file:upload").await?;
    service::upload::abort(&s, &u, &sid).await?;
    Ok(Json(serde_json::json!({})))
}

// ---------- 文件 ----------

pub async fn list_project_files(
    State(s): State<AppState>,
    u: CurrentUser,
    Path(id): Path<u64>,
    Query(q): Query<service::file::FileListQuery>,
) -> ApiResult<Json<Value>> {
    Ok(Json(
        serde_json::to_value(service::file::list(&s.db, &u, id, &q).await?).unwrap(),
    ))
}

pub async fn download_file(
    State(s): State<AppState>,
    u: CurrentUser,
    Path(id): Path<u64>,
) -> ApiResult<Response> {
    require(&s, &u, "file:download").await?;
    service::file::stream_file(&s, &u, id, false).await
}

pub async fn file_content(
    State(s): State<AppState>,
    u: CurrentUser,
    Path(id): Path<u64>,
) -> ApiResult<Response> {
    require(&s, &u, "file:preview").await?;
    service::file::stream_file(&s, &u, id, true).await
}

pub async fn delete_file(
    State(s): State<AppState>,
    u: CurrentUser,
    Path(id): Path<u64>,
) -> ApiResult<Json<Value>> {
    require(&s, &u, "file:delete").await?;
    service::file::delete(&s, &u, id).await?;
    Ok(Json(serde_json::json!({})))
}

pub async fn batch_download(
    State(s): State<AppState>,
    u: CurrentUser,
    Json(req): Json<service::file::BatchDownloadReq>,
) -> ApiResult<Response> {
    require(&s, &u, "file:download").await?;
    service::file::batch_download(&s, &u, &req).await
}
