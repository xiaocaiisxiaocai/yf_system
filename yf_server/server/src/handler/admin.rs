//! 薄 handler：参数解析 → service。权限点在路由层（perm::guard）或 service 首行校验。
use axum::extract::{Path, State};
use axum::Json;
use axum_extra::extract::Query;
use serde_json::Value;

use crate::dto::{PageQuery, PasswordReq, StatusReq};
use crate::error::ApiResult;
use crate::middleware::auth::CurrentUser;
use crate::service;
use crate::state::AppState;

// ---------- 部门 ----------

pub async fn list_departments(
    State(s): State<AppState>,
    _u: CurrentUser,
) -> ApiResult<Json<Value>> {
    Ok(Json(service::dept::list(&s.db).await?))
}
pub async fn create_department(
    State(s): State<AppState>,
    u: CurrentUser,
    Json(req): Json<service::dept::DeptUpsert>,
) -> ApiResult<Json<Value>> {
    Ok(Json(service::dept::create(&s.db, &u, &req).await?))
}
pub async fn update_department(
    State(s): State<AppState>,
    u: CurrentUser,
    Path(id): Path<u64>,
    Json(req): Json<service::dept::DeptUpsert>,
) -> ApiResult<Json<Value>> {
    Ok(Json(service::dept::update(&s.db, &u, id, &req).await?))
}
pub async fn update_department_status(
    State(s): State<AppState>,
    u: CurrentUser,
    Path(id): Path<u64>,
    Json(req): Json<StatusReq>,
) -> ApiResult<Json<Value>> {
    Ok(Json(
        service::dept::set_status(&s.db, &u, id, &req.status).await?,
    ))
}
pub async fn delete_department(
    State(s): State<AppState>,
    u: CurrentUser,
    Path(id): Path<u64>,
) -> ApiResult<Json<Value>> {
    service::dept::delete(&s.db, &u, id).await?;
    Ok(Json(serde_json::json!({})))
}

// ---------- 用户 ----------

pub async fn list_users(
    State(s): State<AppState>,
    _u: CurrentUser,
    Query(q): Query<service::user::UserListQuery>,
) -> ApiResult<Json<Value>> {
    Ok(Json(
        serde_json::to_value(service::user::list(&s.db, &q).await?).unwrap(),
    ))
}
pub async fn list_user_role_options(
    State(s): State<AppState>,
    _u: CurrentUser,
    Query(q): Query<service::user::RoleOptionQuery>,
) -> ApiResult<Json<Value>> {
    Ok(Json(service::user::role_options(&s.db, &q).await?))
}
pub async fn create_user(
    State(s): State<AppState>,
    u: CurrentUser,
    Json(req): Json<service::user::UserCreate>,
) -> ApiResult<Json<Value>> {
    Ok(Json(service::user::create(&s.db, &u, &req).await?))
}
pub async fn update_user(
    State(s): State<AppState>,
    u: CurrentUser,
    Path(id): Path<u64>,
    Json(req): Json<service::user::UserUpdate>,
) -> ApiResult<Json<Value>> {
    Ok(Json(service::user::update(&s.db, &u, id, &req).await?))
}
pub async fn update_user_status(
    State(s): State<AppState>,
    u: CurrentUser,
    Path(id): Path<u64>,
    Json(req): Json<StatusReq>,
) -> ApiResult<Json<Value>> {
    Ok(Json(
        service::user::set_status(&s.db, &u, id, &req.status).await?,
    ))
}
pub async fn reset_user_password(
    State(s): State<AppState>,
    u: CurrentUser,
    Path(id): Path<u64>,
    Json(req): Json<PasswordReq>,
) -> ApiResult<Json<Value>> {
    service::user::reset_password(
        &s.db,
        &u,
        id,
        &service::user::PasswordReset {
            new_password: req.new_password,
        },
    )
    .await?;
    Ok(Json(serde_json::json!({})))
}
pub async fn assign_user_roles(
    State(s): State<AppState>,
    u: CurrentUser,
    Path(id): Path<u64>,
    Json(req): Json<service::user::RoleAssign>,
) -> ApiResult<Json<Value>> {
    service::user::assign_roles(&s.db, &u, id, &req).await?;
    Ok(Json(serde_json::json!({})))
}
pub async fn delete_user(
    State(s): State<AppState>,
    u: CurrentUser,
    Path(id): Path<u64>,
) -> ApiResult<Json<Value>> {
    service::user::delete(&s.db, &u, id).await?;
    Ok(Json(serde_json::json!({})))
}

// ---------- 角色与权限 ----------

pub async fn list_roles(
    State(s): State<AppState>,
    _u: CurrentUser,
    Query(q): Query<PageQuery>,
) -> ApiResult<Json<Value>> {
    Ok(Json(
        serde_json::to_value(service::role::list(&s.db, &q).await?).unwrap(),
    ))
}
pub async fn create_role(
    State(s): State<AppState>,
    u: CurrentUser,
    Json(req): Json<service::role::RoleUpsert>,
) -> ApiResult<Json<Value>> {
    Ok(Json(service::role::create(&s.db, &u, &req).await?))
}
pub async fn update_role(
    State(s): State<AppState>,
    u: CurrentUser,
    Path(id): Path<u64>,
    Json(req): Json<service::role::RoleUpsert>,
) -> ApiResult<Json<Value>> {
    Ok(Json(service::role::update(&s.db, &u, id, &req).await?))
}
pub async fn update_role_status(
    State(s): State<AppState>,
    u: CurrentUser,
    Path(id): Path<u64>,
    Json(req): Json<StatusReq>,
) -> ApiResult<Json<Value>> {
    Ok(Json(
        service::role::set_status(&s.db, &u, id, &req.status).await?,
    ))
}
pub async fn assign_role_permissions(
    State(s): State<AppState>,
    u: CurrentUser,
    Path(id): Path<u64>,
    Json(req): Json<service::role::PermAssign>,
) -> ApiResult<Json<Value>> {
    service::role::assign_permissions(&s.db, &u, id, &req).await?;
    Ok(Json(serde_json::json!({})))
}
pub async fn list_permissions(
    State(s): State<AppState>,
    _u: CurrentUser,
) -> ApiResult<Json<Value>> {
    Ok(Json(service::role::perm_tree(&s.db).await?))
}
pub async fn delete_role(
    State(s): State<AppState>,
    u: CurrentUser,
    Path(id): Path<u64>,
) -> ApiResult<Json<Value>> {
    service::role::delete(&s.db, &u, id).await?;
    Ok(Json(serde_json::json!({})))
}

// ---------- 供应商 ----------

pub async fn list_suppliers(
    State(s): State<AppState>,
    _u: CurrentUser,
    Query(q): Query<service::supplier::SupplierListQuery>,
) -> ApiResult<Json<Value>> {
    Ok(Json(
        serde_json::to_value(service::supplier::list(&s.db, &q).await?).unwrap(),
    ))
}
pub async fn create_supplier(
    State(s): State<AppState>,
    u: CurrentUser,
    Json(req): Json<service::supplier::SupplierUpsert>,
) -> ApiResult<Json<Value>> {
    Ok(Json(service::supplier::create(&s.db, &u, &req).await?))
}
pub async fn get_supplier(
    State(s): State<AppState>,
    _u: CurrentUser,
    Path(id): Path<u64>,
) -> ApiResult<Json<Value>> {
    Ok(Json(service::supplier::detail(&s.db, id).await?))
}
pub async fn update_supplier(
    State(s): State<AppState>,
    u: CurrentUser,
    Path(id): Path<u64>,
    Json(req): Json<service::supplier::SupplierUpsert>,
) -> ApiResult<Json<Value>> {
    Ok(Json(service::supplier::update(&s.db, &u, id, &req).await?))
}
pub async fn update_supplier_status(
    State(s): State<AppState>,
    u: CurrentUser,
    Path(id): Path<u64>,
    Json(req): Json<StatusReq>,
) -> ApiResult<Json<Value>> {
    Ok(Json(
        service::supplier::set_status(&s.db, &u, id, &req.status).await?,
    ))
}
pub async fn delete_supplier(
    State(s): State<AppState>,
    u: CurrentUser,
    Path(id): Path<u64>,
) -> ApiResult<Json<Value>> {
    service::supplier::delete(&s.db, &u, id).await?;
    Ok(Json(serde_json::json!({})))
}
pub async fn list_supplier_accounts(
    State(s): State<AppState>,
    _u: CurrentUser,
    Path(id): Path<u64>,
) -> ApiResult<Json<Value>> {
    Ok(Json(service::supplier::list_accounts(&s.db, id).await?))
}
pub async fn create_supplier_account(
    State(s): State<AppState>,
    u: CurrentUser,
    Path(id): Path<u64>,
    Json(req): Json<service::supplier::AccountCreate>,
) -> ApiResult<Json<Value>> {
    Ok(Json(
        service::supplier::create_account(&s.db, &u, id, &req).await?,
    ))
}
pub async fn update_supplier_account(
    State(s): State<AppState>,
    u: CurrentUser,
    Path(id): Path<u64>,
    Json(req): Json<service::supplier::AccountUpdate>,
) -> ApiResult<Json<Value>> {
    Ok(Json(
        service::supplier::update_account(&s.db, &u, id, &req).await?,
    ))
}
pub async fn update_supplier_account_status(
    State(s): State<AppState>,
    u: CurrentUser,
    Path(id): Path<u64>,
    Json(req): Json<StatusReq>,
) -> ApiResult<Json<Value>> {
    Ok(Json(
        service::supplier::set_account_status(&s.db, &u, id, &req.status).await?,
    ))
}
pub async fn reset_supplier_account_password(
    State(s): State<AppState>,
    u: CurrentUser,
    Path(id): Path<u64>,
    Json(req): Json<PasswordReq>,
) -> ApiResult<Json<Value>> {
    service::supplier::reset_account_password(&s.db, &u, id, &req.new_password).await?;
    Ok(Json(serde_json::json!({})))
}
pub async fn delete_supplier_account(
    State(s): State<AppState>,
    u: CurrentUser,
    Path(id): Path<u64>,
) -> ApiResult<Json<Value>> {
    service::supplier::delete_account(&s.db, &u, id).await?;
    Ok(Json(serde_json::json!({})))
}
