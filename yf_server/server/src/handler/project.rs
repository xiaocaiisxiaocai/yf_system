//! 项目 / 轮次 / 留言 / 工作台 handler。数据范围由 service::scope 在内部强制。
use axum::extract::{Path, State};
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

// ---------- 项目 ----------

pub async fn list_projects(
    State(s): State<AppState>,
    u: CurrentUser,
    Query(q): Query<service::project::ProjectListQuery>,
) -> ApiResult<Json<Value>> {
    Ok(Json(
        serde_json::to_value(service::project::list(&s.db, &u, &q).await?).unwrap(),
    ))
}
pub async fn create_project(
    State(s): State<AppState>,
    u: CurrentUser,
    Json(req): Json<service::project::ProjectUpsert>,
) -> ApiResult<Json<Value>> {
    require(&s, &u, "project:create").await?;
    Ok(Json(service::project::create(&s.db, &u, &req).await?))
}
pub async fn get_project(
    State(s): State<AppState>,
    u: CurrentUser,
    Path(id): Path<u64>,
) -> ApiResult<Json<Value>> {
    Ok(Json(service::project::detail(&s.db, &u, id).await?))
}
pub async fn update_project(
    State(s): State<AppState>,
    u: CurrentUser,
    Path(id): Path<u64>,
    Json(req): Json<service::project::ProjectUpsert>,
) -> ApiResult<Json<Value>> {
    require(&s, &u, "project:update").await?;
    Ok(Json(service::project::update(&s.db, &u, id, &req).await?))
}
pub async fn delete_project(
    State(s): State<AppState>,
    u: CurrentUser,
    Path(id): Path<u64>,
) -> ApiResult<Json<Value>> {
    require(&s, &u, "project:update").await?;
    service::project::delete(&s, &u, id).await?;
    Ok(Json(serde_json::json!({})))
}
pub async fn update_project_status(
    State(s): State<AppState>,
    u: CurrentUser,
    Path(id): Path<u64>,
    Json(req): Json<service::project::StatusChange>,
) -> ApiResult<Json<Value>> {
    require(&s, &u, "project:status").await?;
    Ok(Json(
        service::project::set_status(&s.db, &u, id, &req).await?,
    ))
}
pub async fn list_project_members(
    State(s): State<AppState>,
    u: CurrentUser,
    Path(id): Path<u64>,
) -> ApiResult<Json<Value>> {
    service::scope::ensure_project_access(&s.db, &u, id).await?;
    Ok(Json(service::project::list_members(&s.db, id).await?))
}
pub async fn set_project_members(
    State(s): State<AppState>,
    u: CurrentUser,
    Path(id): Path<u64>,
    Json(req): Json<service::project::MembersSet>,
) -> ApiResult<Json<Value>> {
    require(&s, &u, "project:member").await?;
    service::project::set_members(&s.db, &u, id, &req).await?;
    Ok(Json(serde_json::json!({})))
}
pub async fn project_summary(
    State(s): State<AppState>,
    u: CurrentUser,
    Path(id): Path<u64>,
) -> ApiResult<Json<Value>> {
    Ok(Json(service::project::summary(&s.db, &u, id).await?))
}
pub async fn list_project_activities(
    State(s): State<AppState>,
    u: CurrentUser,
    Path(id): Path<u64>,
    Query(q): Query<service::project_activity::ActivityQuery>,
) -> ApiResult<Json<Value>> {
    Ok(Json(
        service::project_activity::list(&s.db, &u, id, &q).await?,
    ))
}

// ---------- 轮次 ----------

pub async fn list_rounds(
    State(s): State<AppState>,
    u: CurrentUser,
    Path(id): Path<u64>,
) -> ApiResult<Json<Value>> {
    Ok(Json(service::round::list(&s.db, &u, id).await?))
}
pub async fn create_round(
    State(s): State<AppState>,
    u: CurrentUser,
    Path(id): Path<u64>,
    Json(req): Json<service::round::RoundCreate>,
) -> ApiResult<Json<Value>> {
    require(&s, &u, "round:create").await?;
    Ok(Json(service::round::create(&s.db, &u, id, &req).await?))
}
pub async fn get_round(
    State(s): State<AppState>,
    u: CurrentUser,
    Path(id): Path<u64>,
) -> ApiResult<Json<Value>> {
    Ok(Json(service::round::detail(&s.db, &u, id).await?))
}
/// 轮次确认/驳回：内部用户需 round:confirm 权限点；供应商人员走 confirmSide 校验（service::round::check_side）
async fn require_round_decide(s: &AppState, u: &CurrentUser) -> ApiResult<()> {
    if u.is_internal() {
        require(s, u, "round:confirm").await?;
    }
    Ok(())
}

pub async fn confirm_round(
    State(s): State<AppState>,
    u: CurrentUser,
    Path(id): Path<u64>,
) -> ApiResult<Json<Value>> {
    require_round_decide(&s, &u).await?;
    Ok(Json(service::round::confirm(&s.db, &s.cfg, &u, id).await?))
}
pub async fn reject_round(
    State(s): State<AppState>,
    u: CurrentUser,
    Path(id): Path<u64>,
    Json(req): Json<service::round::RejectReq>,
) -> ApiResult<Json<Value>> {
    require_round_decide(&s, &u).await?;
    Ok(Json(
        service::round::reject(&s.db, &s.cfg, &u, id, &req).await?,
    ))
}
pub async fn cancel_round(
    State(s): State<AppState>,
    u: CurrentUser,
    Path(id): Path<u64>,
) -> ApiResult<Json<Value>> {
    require(&s, &u, "round:cancel").await?;
    Ok(Json(service::round::cancel(&s.db, &s.cfg, &u, id).await?))
}

// ---------- 留言 ----------

pub async fn list_messages(
    State(s): State<AppState>,
    u: CurrentUser,
    Path(id): Path<u64>,
    Query(q): Query<service::message::MessageListQuery>,
) -> ApiResult<Json<Value>> {
    Ok(Json(
        serde_json::to_value(service::message::list(&s.db, &u, id, &q).await?).unwrap(),
    ))
}
pub async fn create_message(
    State(s): State<AppState>,
    u: CurrentUser,
    Path(id): Path<u64>,
    Json(req): Json<service::message::MessageCreate>,
) -> ApiResult<Json<Value>> {
    require(&s, &u, "message:create").await?;
    Ok(Json(
        service::message::create(&s.db, &u, id, &req, &s.cfg.web.base_url).await?,
    ))
}
pub async fn mark_read(
    State(s): State<AppState>,
    u: CurrentUser,
    Json(req): Json<service::message::MarkRead>,
) -> ApiResult<Json<Value>> {
    service::message::mark_read(&s.db, &u, &req).await?;
    Ok(Json(serde_json::json!({})))
}
pub async fn message_reads(
    State(s): State<AppState>,
    u: CurrentUser,
    Path(id): Path<u64>,
) -> ApiResult<Json<Value>> {
    Ok(Json(service::message::reads(&s.db, &u, id).await?))
}
pub async fn delete_message(
    State(s): State<AppState>,
    u: CurrentUser,
    Path(id): Path<u64>,
) -> ApiResult<Json<Value>> {
    require(&s, &u, "message:delete_any").await?;
    service::message::delete(&s.db, &u, id).await?;
    Ok(Json(serde_json::json!({})))
}

// ---------- 工作台 ----------

pub async fn dashboard_summary(
    State(s): State<AppState>,
    u: CurrentUser,
) -> ApiResult<Json<Value>> {
    Ok(Json(service::dashboard::summary(&s.db, &u).await?))
}

// ---------- 下拉选项（内部用户） ----------

pub async fn supplier_options(State(s): State<AppState>, u: CurrentUser) -> ApiResult<Json<Value>> {
    Ok(Json(service::project::supplier_options(&s.db, &u).await?))
}
pub async fn internal_user_options(
    State(s): State<AppState>,
    u: CurrentUser,
) -> ApiResult<Json<Value>> {
    Ok(Json(
        service::project::internal_user_options(&s.db, &u).await?,
    ))
}
