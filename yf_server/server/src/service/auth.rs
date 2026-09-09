//! 认证纵切片：登录（含锁定策略）、refresh 旋转、登出、改密、profile。
//! 规则：连续失败 5 次锁 30 分钟；改密/登出吊销 refresh token；供应商禁用则其人员拒绝登录。
use chrono::{Duration, Utc};
use sea_orm::sea_query::Expr;
use sea_orm::{
    ActiveModelTrait, ColumnTrait, Condition, ConnectionTrait, DatabaseConnection, EntityTrait,
    QueryFilter, QuerySelect, Set, TransactionTrait,
};
use serde_json::json;

use crate::config::Config;
use crate::dto::*;
use crate::entity::enums::{CommonStatus, PermissionType, UserType};
use crate::entity::{permissions, refresh_tokens, suppliers, users};
use crate::error::{ApiResult, AppError};
use crate::middleware::auth::CurrentUser;
use crate::util::{jwt, password, token};

use super::audit;

const MAX_FAILED: i32 = 5;

fn lock_active(until: Option<chrono::DateTime<Utc>>, now: chrono::DateTime<Utc>) -> bool {
    until.is_some_and(|value| value > now)
}

/// 返回 (登录响应, refresh token 原文)
pub async fn login(
    db: &DatabaseConnection,
    cfg: &Config,
    req: &LoginRequest,
    ip: Option<String>,
    captchas: &crate::state::CaptchaStore,
) -> ApiResult<(LoginResponse, String)> {
    let txn = db.begin().await?;
    let user = users::Entity::find()
        .filter(users::Column::EmployeeNo.eq(req.employee_no.trim()))
        .lock_exclusive()
        .one(&txn)
        .await?;

    // 统一报错文案，避免工号枚举
    let Some(user) = user else {
        txn.rollback().await?;
        audit::log(
            db,
            None,
            Some(req.employee_no.clone()),
            "LOGIN_FAILED",
            None,
            None,
            None,
            ip,
        )
        .await;
        return Err(AppError::Unauthorized("工号或密码错误".into()));
    };

    let now = Utc::now();
    // 连续失败 3 次后要求图形验证码
    if user.failed_login_attempts >= 3 {
        let ok = match (&req.captcha_id, &req.captcha_code) {
            (Some(id), Some(code)) => super::captcha::verify(captchas, id, code),
            _ => false,
        };
        if !ok {
            return Err(AppError::CaptchaRequired);
        }
    }

    if lock_active(user.locked_until, now) {
        return Err(AppError::Locked);
    }
    if user.status != CommonStatus::Active {
        return Err(AppError::Unauthorized("账号已被禁用".into()));
    }
    if let Some(sid) =
        crate::middleware::auth::supplier_id_for_auth(user.user_type, user.supplier_id)?
    {
        let supplier = suppliers::Entity::find_by_id(sid).one(&txn).await?;
        if supplier.map(|s| s.status) != Some(CommonStatus::Active) {
            return Err(AppError::Unauthorized("所属供应商已被禁用".into()));
        }
    }

    if !password::verify(&req.password, &user.password_hash) {
        // 原子自增，避免并发失败登录丢失计数
        users::Entity::update_many()
            .col_expr(
                users::Column::FailedLoginAttempts,
                Expr::col(users::Column::FailedLoginAttempts).add(1),
            )
            .filter(users::Column::Id.eq(user.id))
            .exec(&txn)
            .await?;
        let attempts = users::Entity::find_by_id(user.id)
            .one(&txn)
            .await?
            .map(|u| u.failed_login_attempts)
            .unwrap_or(0);
        if attempts >= MAX_FAILED {
            // 仅在未锁定状态下落锁，防止并发重复触发
            users::Entity::update_many()
                .col_expr(users::Column::FailedLoginAttempts, Expr::value(0))
                .col_expr(
                    users::Column::LockedUntil,
                    Expr::value(now + Duration::minutes(30)),
                )
                .filter(users::Column::Id.eq(user.id))
                .filter(
                    Condition::any()
                        .add(users::Column::LockedUntil.is_null())
                        .add(users::Column::LockedUntil.lte(now)),
                )
                .exec(&txn)
                .await?;
            txn.commit().await?;
            audit::log(
                db,
                Some(user.id),
                Some(user.employee_no.clone()),
                "LOGIN_LOCKED",
                None,
                None,
                None,
                ip,
            )
            .await;
            return Err(AppError::Locked);
        }
        txn.commit().await?;
        audit::log(
            db,
            Some(user.id),
            Some(user.employee_no.clone()),
            "LOGIN_FAILED",
            None,
            None,
            None,
            ip,
        )
        .await;
        if attempts >= 3 {
            return Err(AppError::CaptchaRequired);
        }
        return Err(AppError::Unauthorized(format!(
            "工号或密码错误（剩余尝试次数 {}）",
            MAX_FAILED - attempts
        )));
    }

    let mut am: users::ActiveModel = user.clone().into();
    am.failed_login_attempts = Set(0);
    am.locked_until = Set(None);
    am.last_login_at = Set(Some(now));
    am.last_login_ip = Set(ip.clone());
    am.update(&txn).await?;

    let (access_token, expires_at) = jwt::issue_access(
        &cfg.jwt.secret,
        user.id,
        &user.employee_no,
        cfg.jwt.access_ttl_minutes,
    )?;
    let refresh = issue_refresh(&txn, user.id, cfg.jwt.refresh_ttl_days, ip.clone()).await?;
    txn.commit().await?;
    audit::log(
        db,
        Some(user.id),
        Some(user.employee_no.clone()),
        "LOGIN",
        None,
        None,
        None,
        ip,
    )
    .await;

    let (perms, menus) = perms_and_menus(db, user.id).await?;
    Ok((
        LoginResponse {
            access_token,
            expires_at,
            must_change_password: user.must_change_password,
            permissions: perms,
            menus,
            user: brief(db, &user).await?,
        },
        refresh,
    ))
}

async fn perms_and_menus(
    db: &DatabaseConnection,
    user_id: u64,
) -> ApiResult<(Vec<String>, Vec<String>)> {
    let perms = super::perm::permission_codes(db, user_id).await?;
    let menus = permissions::Entity::find()
        .filter(permissions::Column::PermType.eq(PermissionType::Menu))
        .all(db)
        .await?
        .into_iter()
        .filter(|p| perms.contains(&p.code))
        .map(|p| p.code)
        .collect();
    Ok((perms, menus))
}

async fn issue_refresh(
    db: &impl ConnectionTrait,
    user_id: u64,
    ttl_days: i64,
    ip: Option<String>,
) -> ApiResult<String> {
    let raw = token::new_refresh_token();
    refresh_tokens::ActiveModel {
        user_id: Set(user_id),
        token_hash: Set(token::hash_token(&raw)),
        expires_at: Set(Utc::now() + Duration::days(ttl_days)),
        revoked: Set(false),
        ip: Set(ip),
        created_at: Set(Utc::now()),
        ..Default::default()
    }
    .insert(db)
    .await?;
    Ok(raw)
}

/// 旋转 refresh：CAS 吊销旧 token，重放（并发/盗用）则吊销该用户全部会话
pub async fn refresh(
    db: &DatabaseConnection,
    cfg: &Config,
    refresh_token: &str,
    ip: Option<String>,
) -> ApiResult<(TokenResponse, String)> {
    let row = refresh_tokens::Entity::find()
        .filter(refresh_tokens::Column::TokenHash.eq(token::hash_token(refresh_token)))
        .one(db)
        .await?
        .ok_or_else(|| AppError::Unauthorized("登录状态无效".into()))?;

    // Account changes lock users before revoking refresh tokens. Keep that order.
    let txn = db.begin().await?;
    let user = users::Entity::find_by_id(row.user_id)
        .lock_exclusive()
        .one(&txn)
        .await?
        .ok_or_else(|| AppError::Unauthorized("账号不存在".into()))?;
    let row = refresh_tokens::Entity::find_by_id(row.id)
        .lock_exclusive()
        .one(&txn)
        .await?
        .ok_or_else(|| AppError::Unauthorized("登录状态无效".into()))?;

    if row.revoked || row.expires_at < Utc::now() {
        // 已吊销的旧 token 再次使用 = 疑似重放/泄露，吊销该用户全部 refresh token
        if row.revoked {
            revoke_all(&txn, row.user_id).await?;
            txn.commit().await?;
            audit::log(
                db,
                Some(row.user_id),
                None,
                "LOGIN_FAILED",
                Some("refresh_token"),
                None,
                Some(json!({"reason": "refresh token reuse detected"})),
                ip,
            )
            .await;
        }
        return Err(AppError::Unauthorized("登录状态已失效，请重新登录".into()));
    }
    if user.status != CommonStatus::Active {
        return Err(AppError::Unauthorized("账号已被禁用".into()));
    }
    if let Some(sid) =
        crate::middleware::auth::supplier_id_for_auth(user.user_type, user.supplier_id)?
    {
        let supplier = suppliers::Entity::find_by_id(sid).one(&txn).await?;
        if supplier.map(|s| s.status) != Some(CommonStatus::Active) {
            return Err(AppError::Unauthorized("所属供应商已被禁用".into()));
        }
    }

    // CAS 吊销：并发刷新只有一个成功，避免一个 refresh 换出多份新凭证
    let upd = refresh_tokens::Entity::update_many()
        .col_expr(refresh_tokens::Column::Revoked, Expr::value(true))
        .filter(refresh_tokens::Column::Id.eq(row.id))
        .filter(refresh_tokens::Column::Revoked.eq(false))
        .exec(&txn)
        .await?;
    if upd.rows_affected == 0 {
        return Err(AppError::Unauthorized("登录状态已失效，请重新登录".into()));
    }

    let (access_token, expires_at) = jwt::issue_access(
        &cfg.jwt.secret,
        user.id,
        &user.employee_no,
        cfg.jwt.access_ttl_minutes,
    )?;
    let new_refresh = issue_refresh(&txn, user.id, cfg.jwt.refresh_ttl_days, ip).await?;
    txn.commit().await?;
    Ok((
        TokenResponse {
            access_token,
            expires_at,
        },
        new_refresh,
    ))
}

pub async fn logout(db: &DatabaseConnection, refresh_token: Option<&str>) -> ApiResult<()> {
    if let Some(raw) = refresh_token {
        refresh_tokens::Entity::update_many()
            .col_expr(refresh_tokens::Column::Revoked, Expr::value(true))
            .filter(refresh_tokens::Column::TokenHash.eq(token::hash_token(raw)))
            .exec(db)
            .await?;
    }
    Ok(())
}

pub async fn change_password(
    db: &DatabaseConnection,
    current: &CurrentUser,
    req: &ChangePasswordRequest,
) -> ApiResult<()> {
    let txn = db.begin().await?;
    let user = users::Entity::find_by_id(current.id)
        .lock_exclusive()
        .one(&txn)
        .await?
        .ok_or(AppError::NotFound)?;
    if user.status != CommonStatus::Active {
        return Err(AppError::Forbidden);
    }
    if !password::verify(&req.old_password, &user.password_hash) {
        return Err(AppError::BadRequest("原密码错误".into()));
    }
    if !password::strong_enough(&req.new_password) {
        return Err(AppError::BadRequest("新密码需 6-20 位".into()));
    }
    let mut am: users::ActiveModel = user.into();
    am.password_hash = Set(password::hash(&req.new_password)?);
    am.must_change_password = Set(false);
    am.updated_at = Set(Utc::now());
    am.update(&txn).await?;
    revoke_all(&txn, current.id).await?;
    audit::insert(
        &txn,
        Some(current.id),
        Some(current.employee_no.clone()),
        "PASSWORD_CHANGE",
        None,
        None,
        None,
        None,
    )
    .await?;
    txn.commit().await?;
    Ok(())
}

/// 改密/禁用账号时吊销该用户全部 refresh token
pub async fn revoke_all(db: &impl ConnectionTrait, user_id: u64) -> ApiResult<()> {
    refresh_tokens::Entity::update_many()
        .col_expr(refresh_tokens::Column::Revoked, Expr::value(true))
        .filter(refresh_tokens::Column::UserId.eq(user_id))
        .exec(db)
        .await?;
    Ok(())
}

pub async fn profile(db: &DatabaseConnection, current: &CurrentUser) -> ApiResult<ProfileResponse> {
    let user = users::Entity::find_by_id(current.id)
        .one(db)
        .await?
        .ok_or(AppError::NotFound)?;
    let (perms, menus) = perms_and_menus(db, user.id).await?;
    Ok(ProfileResponse {
        user: brief(db, &user).await?,
        must_change_password: user.must_change_password,
        permissions: perms,
        menus,
    })
}

pub async fn update_profile(
    db: &DatabaseConnection,
    current: &CurrentUser,
    req: &UpdateProfileRequest,
) -> ApiResult<ProfileResponse> {
    crate::util::validation::email(&req.email)?;
    let email = req.email.trim().to_string();
    let txn = db.begin().await?;
    let user = users::Entity::find_by_id(current.id)
        .lock_exclusive()
        .one(&txn)
        .await?
        .ok_or(AppError::NotFound)?;
    if user.status != CommonStatus::Active {
        return Err(AppError::Forbidden);
    }
    let changed = user.email != email;
    if changed {
        let mut am: users::ActiveModel = user.into();
        am.email = Set(email);
        am.updated_at = Set(Utc::now());
        am.update(&txn).await?;
        audit::insert(
            &txn,
            Some(current.id),
            Some(current.employee_no.clone()),
            "PROFILE_UPDATE",
            Some("user"),
            Some(current.id.to_string()),
            Some(json!({ "changedFields": ["email"] })),
            None,
        )
        .await?;
    }
    txn.commit().await?;
    profile(db, current).await
}

async fn brief(db: &DatabaseConnection, user: &users::Model) -> ApiResult<UserBrief> {
    Ok(UserBrief {
        id: user.id,
        employee_no: user.employee_no.clone(),
        real_name: user.real_name.clone(),
        email: user.email.clone(),
        user_type: user.user_type.as_str().to_string(),
        supplier_id: user.supplier_id,
        is_system_admin: user.status == CommonStatus::Active
            && user.user_type == UserType::Internal
            && super::scope::is_system_admin(db, user.id).await?,
    })
}

#[cfg(test)]
mod tests {
    use super::lock_active;
    use chrono::{Duration, Utc};

    #[test]
    fn expired_lock_is_not_active_and_can_be_rearmed() {
        let now = Utc::now();
        assert!(!lock_active(Some(now - Duration::seconds(1)), now));
        assert!(lock_active(Some(now + Duration::seconds(1)), now));
    }
}
