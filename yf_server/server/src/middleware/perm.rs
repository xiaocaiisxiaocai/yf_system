//! 权限点校验。在 handler::router 中对受保护路由分组使用：
//! ```ignore
//! use axum::middleware::from_fn_with_state;
//! .layer(from_fn_with_state((state.clone(), "user:manage"), perm::guard))
//! ```
//! 注意：权限点只解决"能不能做"，数据范围过滤由 service 层另行保证。
use axum::{
    extract::{Request, State},
    middleware::Next,
    response::Response,
};

use super::auth::CurrentUser;
use crate::error::{ApiResult, AppError};
use crate::service;
use crate::state::AppState;

pub type PermState = (AppState, &'static str);

pub async fn guard(
    State((state, code)): State<PermState>,
    req: Request,
    next: Next,
) -> ApiResult<Response> {
    let user = req
        .extensions()
        .get::<CurrentUser>()
        .cloned()
        .ok_or_else(|| AppError::Unauthorized("缺少登录凭证".into()))?;
    service::perm::check_perm(&state.db, user.id, code).await?;
    Ok(next.run(req).await)
}
