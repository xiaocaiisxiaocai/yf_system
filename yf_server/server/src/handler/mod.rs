pub mod admin;
pub mod auth;
pub mod file;
pub mod project;
pub mod system;

use axum::extract::DefaultBodyLimit;
use axum::extract::State;
use axum::http::StatusCode;
use axum::middleware::from_fn_with_state;
use axum::routing::{delete, get, post, put};
use axum::{Json, Router};
use tower_http::cors::CorsLayer;
use tower_http::trace::TraceLayer;

use crate::dto::CaptchaResp;
use crate::middleware::{auth as mw_auth, perm};
use crate::service;
use crate::state::AppState;

pub fn router(state: AppState) -> Router {
    // ---- 公开路由 ----
    let public = Router::new()
        .route("/auth/login", post(auth::login))
        .route("/auth/refresh", post(auth::refresh))
        .route("/auth/captcha", get(captcha));

    // ---- 受保护路由（仅登录，数据范围由 service 层强制）----
    let authed = Router::new()
        .route("/auth/logout", post(auth::logout))
        .route("/auth/profile", get(auth::profile))
        .route("/auth/password", put(auth::change_password))
        .route("/dashboard/summary", get(project::dashboard_summary))
        // 部门树供用户表单使用，读取不卡权限点
        .route("/departments", get(admin::list_departments))
        .route("/permissions", get(admin::list_permissions))
        // 内部用户下拉选项（新建项目/设置成员用）
        .route("/supplier-options", get(project::supplier_options))
        .route(
            "/internal-user-options",
            get(project::internal_user_options),
        )
        // 项目（GET 走数据范围）
        .route(
            "/projects",
            get(project::list_projects).post(project::create_project),
        )
        .route(
            "/projects/{id}",
            get(project::get_project)
                .put(project::update_project)
                .delete(project::delete_project),
        )
        .route("/projects/{id}/status", put(project::update_project_status))
        .route(
            "/projects/{id}/members",
            get(project::list_project_members).put(project::set_project_members),
        )
        .route(
            "/projects/{id}/supplier-members",
            get(project::list_project_supplier_members),
        )
        .route("/projects/{id}/summary", get(project::project_summary))
        .route(
            "/projects/{id}/activities",
            get(project::list_project_activities),
        )
        .route(
            "/projects/{id}/rounds",
            get(project::list_rounds).post(project::create_round),
        )
        .route("/projects/{id}/files", get(file::list_project_files))
        .route(
            "/projects/{id}/messages",
            get(project::list_messages).post(project::create_message),
        )
        .route("/rounds/{id}", get(project::get_round))
        .route("/rounds/{id}/confirm", post(project::confirm_round))
        .route("/rounds/{id}/reject", post(project::reject_round))
        .route("/rounds/{id}/cancel", post(project::cancel_round))
        // 文件与分片上传（分片请求体上限 64MB）
        .route("/uploads/init", post(file::init_upload))
        .route(
            "/uploads/{sid}",
            get(file::get_upload).delete(file::abort_upload),
        )
        .route(
            "/uploads/{sid}/chunks/{index}",
            put(file::upload_chunk).layer(DefaultBodyLimit::max(64 * 1024 * 1024)),
        )
        .route("/uploads/{sid}/merge", post(file::merge_upload))
        .route("/files/{id}/download", get(file::download_file))
        .route("/files/{id}/content", get(file::file_content))
        .route("/files/{id}", delete(file::delete_file))
        .route("/files/batch-download", post(file::batch_download))
        .route("/messages/read", post(project::mark_read))
        .route("/messages/{id}/reads", get(project::message_reads))
        .route("/messages/{id}", delete(project::delete_message));

    // ---- 管理分组：权限点中间件 ----
    let admin_users = Router::new()
        .route("/users", get(admin::list_users).post(admin::create_user))
        .route("/user-role-options", get(admin::list_user_role_options))
        .route(
            "/users/{id}",
            put(admin::update_user).delete(admin::delete_user),
        )
        .route("/users/{id}/status", put(admin::update_user_status))
        .route("/users/{id}/password", put(admin::reset_user_password))
        .route("/users/{id}/roles", put(admin::assign_user_roles))
        .layer(from_fn_with_state(
            (state.clone(), "user:manage"),
            perm::guard,
        ));

    let admin_depts = Router::new()
        .route("/departments", post(admin::create_department))
        .route(
            "/departments/{id}",
            put(admin::update_department).delete(admin::delete_department),
        )
        .route(
            "/departments/{id}/status",
            put(admin::update_department_status),
        )
        .layer(from_fn_with_state(
            (state.clone(), "dept:manage"),
            perm::guard,
        ));

    let admin_roles = Router::new()
        .route("/roles", get(admin::list_roles).post(admin::create_role))
        .route(
            "/roles/{id}",
            put(admin::update_role).delete(admin::delete_role),
        )
        .route("/roles/{id}/status", put(admin::update_role_status))
        .route(
            "/roles/{id}/permissions",
            put(admin::assign_role_permissions),
        )
        .layer(from_fn_with_state(
            (state.clone(), "role:manage"),
            perm::guard,
        ));

    let admin_suppliers = Router::new()
        .route(
            "/suppliers",
            get(admin::list_suppliers).post(admin::create_supplier),
        )
        .route(
            "/suppliers/{id}",
            get(admin::get_supplier)
                .put(admin::update_supplier)
                .delete(admin::delete_supplier),
        )
        .route("/suppliers/{id}/status", put(admin::update_supplier_status))
        .layer(from_fn_with_state(
            (state.clone(), "supplier:manage"),
            perm::guard,
        ));

    let admin_supplier_accounts = Router::new()
        .route(
            "/suppliers/{id}/accounts",
            get(admin::list_supplier_accounts).post(admin::create_supplier_account),
        )
        .route(
            "/supplier-accounts/{id}",
            put(admin::update_supplier_account).delete(admin::delete_supplier_account),
        )
        .route(
            "/supplier-accounts/{id}/status",
            put(admin::update_supplier_account_status),
        )
        .route(
            "/supplier-accounts/{id}/password",
            put(admin::reset_supplier_account_password),
        )
        .layer(from_fn_with_state(
            (state.clone(), "supplier:account"),
            perm::guard,
        ));

    let admin_logs = Router::new()
        .route("/audit-logs", get(system::list_audit_logs))
        .route(
            "/audit-logs/batch-delete",
            post(system::batch_delete_audit_logs),
        )
        .route("/audit-logs/{id}", delete(system::delete_audit_log))
        .layer(from_fn_with_state((state.clone(), "log:view"), perm::guard));

    let admin_system = Router::new()
        .route(
            "/system/configs",
            get(system::get_configs).put(system::update_configs),
        )
        .route("/system/storage", get(system::storage_status))
        .layer(from_fn_with_state(
            (state.clone(), "config:manage"),
            perm::guard,
        ));

    let admin = Router::new()
        .merge(admin_users)
        .merge(admin_depts)
        .merge(admin_roles)
        .merge(admin_suppliers)
        .merge(admin_supplier_accounts)
        .merge(admin_logs)
        .merge(admin_system);

    let protected = authed
        .merge(Router::new().nest("/admin", admin))
        // 认证中间件最后挂：层执行顺序外先内后，保证 CurrentUser 先于权限中间件注入
        .layer(from_fn_with_state(state.clone(), mw_auth::middleware));

    Router::new()
        .route("/health", get(health))
        .nest("/api/v1", public.merge(protected))
        .layer(TraceLayer::new_for_http())
        // 开发期放开；生产由 IIS 同源托管前端后可移除
        .layer(CorsLayer::permissive())
        .with_state(state)
}

async fn health(State(state): State<AppState>) -> (StatusCode, Json<serde_json::Value>) {
    match state.db.ping().await {
        Ok(()) => (
            StatusCode::OK,
            Json(serde_json::json!({ "status": "ok", "db": "up" })),
        ),
        Err(_) => (
            StatusCode::SERVICE_UNAVAILABLE,
            Json(serde_json::json!({ "status": "degraded", "db": "down" })),
        ),
    }
}

async fn captcha(State(state): State<AppState>) -> Json<CaptchaResp> {
    let (captcha_id, svg) = service::captcha::issue(&state.captchas);
    Json(CaptchaResp { captcha_id, svg })
}
