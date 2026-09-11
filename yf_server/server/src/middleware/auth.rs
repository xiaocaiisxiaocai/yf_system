//! JWT 认证中间件：解析 Bearer token → 校验账号/供应商启用状态 → 注入 CurrentUser
use axum::{
    extract::{Request, State},
    http::request::Parts,
    middleware::Next,
    response::Response,
};
use chrono::Utc;
use sea_orm::{ColumnTrait, EntityTrait, QueryFilter};

use crate::entity::enums::{CommonStatus, UserType};
use crate::entity::{refresh_tokens, suppliers, users};
use crate::error::{ApiResult, AppError};
use crate::state::AppState;
use crate::util::jwt;

#[derive(Debug, Clone)]
pub struct CurrentUser {
    pub id: u64,
    pub employee_no: String,
    pub user_type: UserType,
    pub supplier_id: Option<u64>,
}

pub(crate) fn supplier_id_for_auth(
    user_type: UserType,
    supplier_id: Option<u64>,
) -> ApiResult<Option<u64>> {
    match user_type {
        UserType::Internal => Ok(None),
        UserType::Supplier => supplier_id
            .map(Some)
            .ok_or_else(|| AppError::Unauthorized("所属供应商已被禁用".into())),
    }
}

pub(crate) async fn ensure_active_session(
    db: &sea_orm::DatabaseConnection,
    user_id: u64,
    session_id: &str,
) -> ApiResult<()> {
    let active = refresh_tokens::Entity::find()
        .filter(refresh_tokens::Column::SessionId.eq(session_id))
        .filter(refresh_tokens::Column::UserId.eq(user_id))
        .filter(refresh_tokens::Column::Revoked.eq(false))
        .filter(refresh_tokens::Column::ExpiresAt.gt(Utc::now()))
        .one(db)
        .await?
        .is_some();
    if active {
        Ok(())
    } else {
        Err(AppError::Unauthorized("登录状态已失效，请重新登录".into()))
    }
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
    ensure_active_session(&state.db, claims.uid, &claims.sid).await?;
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
    if let Some(sid) = supplier_id_for_auth(user.user_type, user.supplier_id)? {
        let supplier = suppliers::Entity::find_by_id(sid).one(&state.db).await?;
        if supplier.map(|s| s.status) != Some(CommonStatus::Active) {
            return Err(AppError::Unauthorized("所属供应商已被禁用".into()));
        }
    }

    req.extensions_mut().insert(CurrentUser {
        id: user.id,
        employee_no: user.employee_no,
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

#[cfg(test)]
mod tests {
    use super::supplier_id_for_auth;
    use crate::{entity::enums::UserType, error::AppError};

    #[test]
    fn supplier_without_owner_is_rejected() {
        assert!(matches!(
            supplier_id_for_auth(UserType::Supplier, None),
            Err(AppError::Unauthorized(_))
        ));
    }
}
