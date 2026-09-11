//! 认证纵切片：验证码登录、refresh 旋转、持久会话撤销、改密、profile。
//! 规则：每次登录都验证一次图形挑战；匿名失败不锁账号；改密吊销全部会话，登出只吊销当前会话族。
use argon2::password_hash::{PasswordHasher, SaltString};
use argon2::Argon2;
use chrono::{Duration, Utc};
use sea_orm::sea_query::Expr;
use sea_orm::{
    ActiveModelTrait, ColumnTrait, ConnectionTrait, DatabaseConnection, EntityTrait, QueryFilter,
    QuerySelect, Set, TransactionTrait,
};
use serde_json::json;
use std::sync::LazyLock;

use crate::config::Config;
use crate::dto::*;
use crate::entity::enums::{CommonStatus, PermissionType, UserType};
use crate::entity::{permissions, refresh_tokens, suppliers, users};
use crate::error::{ApiResult, AppError};
use crate::middleware::auth::CurrentUser;
use crate::util::{jwt, password, token};

use super::audit;

/// A real Argon2id hash keeps unknown-user verification on the same cost path as known users.
/// It is initialized on the first login attempt regardless of whether that username exists.
static DUMMY_PASSWORD_HASH: LazyLock<String> = LazyLock::new(|| {
    let salt = SaltString::encode_b64(b"yf-login-dummy-salt")
        .expect("the fixed dummy-login salt is a valid PHC salt");
    Argon2::default()
        .hash_password(b"constant-dummy-login-password", &salt)
        .expect("the fixed dummy-login password can be hashed")
        .to_string()
});

fn invalid_credentials() -> AppError {
    AppError::Unauthorized("工号或密码错误".into())
}

/// 返回 (登录响应, refresh token 原文)
pub async fn login(
    db: &DatabaseConnection,
    cfg: &Config,
    req: &LoginRequest,
    ip: Option<String>,
    captchas: &crate::state::CaptchaStore,
) -> ApiResult<(LoginResponse, String)> {
    // A challenge is required for every account and every attempt. This preserves one response
    // path for unknown, disabled and formerly locked accounts while IP limits bound issuance and
    // login attempts in the handler.
    let challenge_ok = match (&req.captcha_id, &req.captcha_code) {
        (Some(id), Some(code)) => super::captcha::verify(captchas, id, code),
        _ => false,
    };
    if !challenge_ok {
        return Err(AppError::CaptchaRequired);
    }

    let candidate = users::Entity::find()
        .filter(users::Column::EmployeeNo.eq(req.employee_no.trim()))
        .one(db)
        .await?;
    // Force initialization on every process's first login before selecting either hash.
    let dummy_hash = DUMMY_PASSWORD_HASH.as_str();
    let password_matches = password::verify(
        &req.password,
        candidate
            .as_ref()
            .map(|user| user.password_hash.as_str())
            .unwrap_or(dummy_hash),
    );
    let failed_user_id = candidate.as_ref().map(|user| user.id);
    let failed_employee_no = candidate
        .as_ref()
        .map(|user| user.employee_no.clone())
        .unwrap_or_else(|| req.employee_no.clone());
    let Some(candidate) = candidate.filter(|_| password_matches) else {
        audit::log(
            db,
            failed_user_id,
            Some(failed_employee_no),
            "LOGIN_FAILED",
            None,
            None,
            None,
            ip,
        )
        .await;
        return Err(invalid_credentials());
    };

    // Re-read under a row lock after the expensive password check so an admin reset/disable and
    // login cannot cross. Wrong guesses never take the account lock.
    let txn = db.begin().await?;
    let user = users::Entity::find_by_id(candidate.id)
        .lock_exclusive()
        .one(&txn)
        .await?
        .ok_or_else(invalid_credentials)?;
    let now = Utc::now();
    let supplier_active = match (user.user_type, user.supplier_id) {
        (UserType::Internal, _) => true,
        (UserType::Supplier, Some(sid)) => suppliers::Entity::find_by_id(sid)
            .one(&txn)
            .await?
            .is_some_and(|supplier| supplier.status == CommonStatus::Active),
        (UserType::Supplier, None) => false,
    };
    if !password::verify(&req.password, &user.password_hash)
        || user.status != CommonStatus::Active
        || !supplier_active
    {
        txn.rollback().await?;
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
        return Err(invalid_credentials());
    }

    let mut am: users::ActiveModel = user.clone().into();
    am.failed_login_attempts = Set(0);
    am.locked_until = Set(None);
    am.last_login_at = Set(Some(now));
    am.last_login_ip = Set(ip.clone());
    am.update(&txn).await?;

    let session_id = uuid::Uuid::new_v4().to_string();
    let refresh = issue_refresh(
        &txn,
        user.id,
        &session_id,
        cfg.jwt.refresh_ttl_days,
        ip.clone(),
    )
    .await?;
    let (access_token, expires_at) = jwt::issue_access(
        &cfg.jwt.secret,
        user.id,
        &user.employee_no,
        &session_id,
        cfg.jwt.access_ttl_minutes,
    )?;
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
    session_id: &str,
    ttl_days: i64,
    ip: Option<String>,
) -> ApiResult<String> {
    let raw = token::new_refresh_token();
    refresh_tokens::ActiveModel {
        user_id: Set(user_id),
        session_id: Set(session_id.to_string()),
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

/// 旋转 refresh：CAS 吊销旧 token；重放仅吊销被泄露的会话族。
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
        // 已吊销的旧 token 再次使用 = 疑似重放/泄露，吊销该会话族。
        if row.revoked {
            revoke_session(&txn, row.user_id, &row.session_id).await?;
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

    let new_refresh =
        issue_refresh(&txn, user.id, &row.session_id, cfg.jwt.refresh_ttl_days, ip).await?;
    let (access_token, expires_at) = jwt::issue_access(
        &cfg.jwt.secret,
        user.id,
        &user.employee_no,
        &row.session_id,
        cfg.jwt.access_ttl_minutes,
    )?;
    txn.commit().await?;
    Ok((
        TokenResponse {
            access_token,
            expires_at,
        },
        new_refresh,
    ))
}

pub async fn logout(
    db: &DatabaseConnection,
    refresh_token: Option<&str>,
    access_session: Option<&(u64, String)>,
) -> ApiResult<()> {
    let cookie_session = if let Some(raw) = refresh_token {
        refresh_tokens::Entity::find()
            .filter(refresh_tokens::Column::TokenHash.eq(token::hash_token(raw)))
            .one(db)
            .await?
            .map(|row| (row.user_id, row.session_id))
    } else {
        None
    };
    let mut targets = Vec::with_capacity(2);
    if let Some(target) = cookie_session {
        targets.push(target);
    }
    if let Some(target) = access_session {
        targets.push(target.clone());
    }
    targets.sort_unstable();
    targets.dedup();
    if targets.is_empty() {
        return Ok(());
    }

    let txn = db.begin().await?;
    // Match refresh/change-password lock ordering. Sorting user ids also prevents two valid
    // credentials from producing opposite lock orders.
    let mut user_ids: Vec<u64> = targets.iter().map(|(user_id, _)| *user_id).collect();
    user_ids.sort_unstable();
    user_ids.dedup();
    for user_id in user_ids {
        users::Entity::find_by_id(user_id)
            .lock_exclusive()
            .one(&txn)
            .await?;
    }
    for (user_id, session_id) in targets {
        revoke_session(&txn, user_id, &session_id).await?;
    }
    txn.commit().await?;
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
        return Err(AppError::BadRequest(password::POLICY_MESSAGE.into()));
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

async fn revoke_session(
    db: &impl ConnectionTrait,
    user_id: u64,
    session_id: &str,
) -> ApiResult<()> {
    refresh_tokens::Entity::update_many()
        .col_expr(refresh_tokens::Column::Revoked, Expr::value(true))
        .filter(refresh_tokens::Column::UserId.eq(user_id))
        .filter(refresh_tokens::Column::SessionId.eq(session_id))
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
    use super::DUMMY_PASSWORD_HASH;

    #[test]
    fn dummy_password_hash_uses_the_normal_verifier() {
        assert!(crate::util::password::verify(
            "constant-dummy-login-password",
            DUMMY_PASSWORD_HASH.as_str()
        ));
    }
}
