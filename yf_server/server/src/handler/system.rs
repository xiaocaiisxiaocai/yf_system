//! 日志查询 / 系统参数 / 存储容量 handler
use axum::extract::State;
use axum::Json;
use axum_extra::extract::Query;
use serde_json::Value;

use crate::error::ApiResult;
use crate::middleware::auth::CurrentUser;
use crate::service;
use crate::state::AppState;

pub async fn list_audit_logs(
    State(s): State<AppState>,
    _u: CurrentUser,
    Query(q): Query<service::log::LogQuery>,
) -> ApiResult<Json<Value>> {
    Ok(Json(
        serde_json::to_value(service::log::list(&s.db, &q).await?).unwrap(),
    ))
}

pub async fn get_configs(State(s): State<AppState>, _u: CurrentUser) -> ApiResult<Json<Value>> {
    Ok(Json(service::config::list(&s.db).await?))
}

pub async fn update_configs(
    State(s): State<AppState>,
    u: CurrentUser,
    Json(req): Json<service::config::ConfigBatch>,
) -> ApiResult<Json<Value>> {
    service::config::update(&s.db, &u, &req).await?;
    Ok(Json(serde_json::json!({})))
}

pub async fn storage_status(State(s): State<AppState>, _u: CurrentUser) -> ApiResult<Json<Value>> {
    Ok(Json(service::config::storage_status(&s).await?))
}
