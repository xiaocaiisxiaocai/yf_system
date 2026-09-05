use axum::extract::{ConnectInfo, State};
use axum::Json;
use axum_extra::extract::cookie::{Cookie, CookieJar, SameSite};
use std::net::SocketAddr;

use crate::dto::*;
use crate::error::{ApiResult, AppError};
use crate::middleware::auth::CurrentUser;
use crate::service;
use crate::state::AppState;

const REFRESH_COOKIE: &str = "refresh_token";

fn build_refresh_cookie(state: &AppState, raw: String) -> Cookie<'static> {
    Cookie::build((REFRESH_COOKIE, raw))
        .http_only(true)
        .same_site(SameSite::Lax)
        .secure(state.cfg.jwt.cookie_secure)
        .path("/api/v1/auth")
        .max_age(time::Duration::days(state.cfg.jwt.refresh_ttl_days))
        .build()
}

fn clear_refresh_cookie() -> Cookie<'static> {
    Cookie::build((REFRESH_COOKIE, ""))
        .http_only(true)
        .path("/api/v1/auth")
        .max_age(time::Duration::ZERO)
        .build()
}

pub async fn login(
    State(state): State<AppState>,
    ConnectInfo(ip): ConnectInfo<SocketAddr>,
    jar: CookieJar,
    Json(req): Json<LoginRequest>,
) -> ApiResult<(CookieJar, Json<LoginResponse>)> {
    if req.username.trim().is_empty() || req.password.is_empty() {
        return Err(AppError::BadRequest("用户名和密码不能为空".into()));
    }
    if req.username.chars().count() > 64
        || req.password.len() > crate::util::password::MAX_PASSWORD_BYTES
    {
        return Err(AppError::BadRequest("用户名或密码错误".into()));
    }
    // 登录限流：同账号同 IP 每分钟 10 次（防单账号爆破）；单 IP 每分钟 60 次（防批量扫号）。
    // 账号级连续失败锁定/验证码策略在 service 层兜底。
    let minute = std::time::Duration::from_secs(60);
    let client_ip = ip.ip();
    let per_account = format!("login:{client_ip}:{}", req.username.trim());
    let per_ip = format!("login:{client_ip}");
    if !state.rate_allow(&per_account, 10, minute) || !state.rate_allow(&per_ip, 60, minute) {
        return Err(AppError::BadRequest("请求过于频繁，请稍后再试".into()));
    }
    let (resp, refresh) = service::auth::login(
        &state.db,
        &state.cfg,
        &req,
        Some(ip.ip().to_string()),
        &state.captchas,
    )
    .await?;
    let jar = jar.add(build_refresh_cookie(&state, refresh));
    Ok((jar, Json(resp)))
}

pub async fn refresh(
    State(state): State<AppState>,
    ConnectInfo(ip): ConnectInfo<SocketAddr>,
    jar: CookieJar,
) -> ApiResult<(CookieJar, Json<TokenResponse>)> {
    let raw = jar
        .get(REFRESH_COOKIE)
        .map(|c| c.value().to_string())
        .ok_or_else(|| AppError::Unauthorized("缺少登录凭证".into()))?;
    let (resp, new_refresh) =
        service::auth::refresh(&state.db, &state.cfg, &raw, Some(ip.ip().to_string())).await?;
    let jar = jar.add(build_refresh_cookie(&state, new_refresh));
    Ok((jar, Json(resp)))
}

pub async fn logout(
    State(state): State<AppState>,
    jar: CookieJar,
) -> ApiResult<(CookieJar, Json<serde_json::Value>)> {
    let raw = jar.get(REFRESH_COOKIE).map(|c| c.value().to_string());
    service::auth::logout(&state.db, raw.as_deref()).await?;
    Ok((
        jar.remove(clear_refresh_cookie()),
        Json(serde_json::json!({})),
    ))
}

pub async fn change_password(
    State(state): State<AppState>,
    current: CurrentUser,
    Json(req): Json<ChangePasswordRequest>,
) -> ApiResult<Json<serde_json::Value>> {
    service::auth::change_password(&state.db, &current, &req).await?;
    Ok(Json(serde_json::json!({})))
}

pub async fn profile(
    State(state): State<AppState>,
    current: CurrentUser,
) -> ApiResult<Json<ProfileResponse>> {
    Ok(Json(service::auth::profile(&state.db, &current).await?))
}
