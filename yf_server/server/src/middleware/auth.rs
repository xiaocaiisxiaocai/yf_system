//! JWT 认证中间件：解析 Bearer token → 校验账号/供应商启用状态 → 注入 CurrentUser
use axum::{
    extract::{Request, State},
    http::request::Parts,
    middleware::Next,
    response::Response,
};
use sea_orm::EntityTrait;

use crate::entity::enums::{CommonStatus, UserType};
use crate::entity::{suppliers, users};
use crate::error::{ApiResult, AppError};
use crate::state::AppState;
use crate::util::jwt;

#[derive(Debug, Clone)]
pub struct CurrentUser {
    pub id: u64,
    pub username: String,
    pub user_type: UserType,
    pub supplier_id: Option<u64>,
}

impl CurrentUser {
    pub fn is_internal(&self) -> bool {
        self.user_type == UserType::Internal
    }
}

/// 挂在受保护路由分组上（见 handler::router）
pub async fn middleware(
    State(state): State<AppState>,
    mut req: Request,
    next: Next,
) -> ApiResult<Response> {
    let header = req
        .headers()
        .get(axum::http::header::AUTHORIZATION)
        .and_then(|v| v.to_str().ok())
        .unwrap_or("");
    let token = header
        .strip_prefix("Bearer ")
        .ok_or_else(|| AppError::Unauthorized("缺少登录凭证".into()))?;

    let claims = jwt::parse_access(&state.cfg.jwt.secret, token)?;
    let user = users::Entity::find_by_id(claims.uid)
        .one(&state.db)
        .await?
        .ok_or_else(|| AppError::Unauthorized("账号不存在".into()))?;

    if user.status != CommonStatus::Active {
        return Err(AppError::Unauthorized("账号已被禁用".into()));
    }
    // 强制改密：除 profile/改密/登出外，其余接口一律拒绝（前端路由守卫可被绕过，必须服务端兜底）
    if user.must_change_password {
        // nest 会剥离 /api/v1 前缀，两种形态都兼容
        let path = req.uri().path();
        let path = path.strip_prefix("/api/v1").unwrap_or(path);
        let allowed = matches!(path, "/auth/profile" | "/auth/password" | "/auth/logout");
        if !allowed {
            return Err(AppError::MustChangePassword);
        }
    }
    // 供应商被禁用时，其全部人员拒绝访问
    if user.user_type == UserType::Supplier {
        if let Some(sid) = user.supplier_id {
            let supplier = suppliers::Entity::find_by_id(sid).one(&state.db).await?;
            if supplier.map(|s| s.status) != Some(CommonStatus::Active) {
                return Err(AppError::Unauthorized("所属供应商已被禁用".into()));
            }
        }
    }

    req.extensions_mut().insert(CurrentUser {
        id: user.id,
        username: user.username,
        user_type: user.user_type,
        supplier_id: user.supplier_id,
    });
    Ok(next.run(req).await)
}

/// 供 handler 以提取器方式取当前登录人（受保护路由上由中间件保证存在）
impl axum::extract::FromRequestParts<AppState> for CurrentUser {
    type Rejection = AppError;

    async fn from_request_parts(
        parts: &mut Parts,
        _state: &AppState,
    ) -> Result<Self, Self::Rejection> {
        parts
            .extensions
            .get::<CurrentUser>()
            .cloned()
            .ok_or_else(|| AppError::Unauthorized("缺少登录凭证".into()))
    }
}
