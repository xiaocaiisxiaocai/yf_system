use axum::extract::{ConnectInfo, State};
use axum::http::{header::HeaderName, HeaderMap};
use axum::Json;
use axum_extra::extract::cookie::{Cookie, CookieJar, SameSite};
use std::net::{IpAddr, SocketAddr};

use crate::dto::*;
use crate::error::{ApiResult, AppError};
use crate::middleware::auth::CurrentUser;
use crate::service;
use crate::state::AppState;

const REFRESH_COOKIE: &str = "refresh_token";
const X_FORWARDED_FOR: HeaderName = HeaderName::from_static("x-forwarded-for");

fn resolve_client_ip(peer: SocketAddr, headers: &HeaderMap, trust_loopback_proxy: bool) -> IpAddr {
    if !trust_loopback_proxy || !peer.ip().is_loopback() {
        return peer.ip();
    }
    let mut forwarded_values = headers.get_all(&X_FORWARDED_FOR).iter();
    let Some(value) = forwarded_values.next() else {
        return peer.ip();
    };
    if forwarded_values.next().is_some() {
        return peer.ip();
    }
    let Ok(raw) = value.to_str() else {
        return peer.ip();
    };
    let Ok(forwarded) = raw.parse::<IpAddr>() else {
        return peer.ip();
    };
    if raw == forwarded.to_string() {
        forwarded
    } else {
        peer.ip()
    }
}

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
    headers: HeaderMap,
    jar: CookieJar,
    Json(req): Json<LoginRequest>,
) -> ApiResult<(CookieJar, Json<LoginResponse>)> {
    if req.employee_no.trim().is_empty() || req.password.is_empty() {
        return Err(AppError::BadRequest("工号和密码不能为空".into()));
    }
    if req.employee_no.chars().count() > 64
        || req.password.len() > crate::util::password::MAX_PASSWORD_BYTES
    {
        return Err(AppError::BadRequest("工号或密码错误".into()));
    }
    // 登录限流：同账号同 IP 每分钟 10 次（防单账号爆破）；单 IP 每分钟 60 次（防批量扫号）。
    // 账号级连续失败锁定/验证码策略在 service 层兜底。
    let minute = std::time::Duration::from_secs(60);
    let client_ip = resolve_client_ip(ip, &headers, state.cfg.server.trust_loopback_proxy);
    let per_account = format!("login:{client_ip}:{}", req.employee_no.trim());
    let per_ip = format!("login:{client_ip}");
    if !state.rate_allow(&per_account, 10, minute) || !state.rate_allow(&per_ip, 60, minute) {
        return Err(AppError::BadRequest("请求过于频繁，请稍后再试".into()));
    }
    let (resp, refresh) = service::auth::login(
        &state.db,
        &state.cfg,
        &req,
        Some(client_ip.to_string()),
        &state.captchas,
    )
    .await?;
    let jar = jar.add(build_refresh_cookie(&state, refresh));
    Ok((jar, Json(resp)))
}

pub async fn refresh(
    State(state): State<AppState>,
    ConnectInfo(ip): ConnectInfo<SocketAddr>,
    headers: HeaderMap,
    jar: CookieJar,
) -> ApiResult<(CookieJar, Json<TokenResponse>)> {
    let raw = jar
        .get(REFRESH_COOKIE)
        .map(|c| c.value().to_string())
        .ok_or_else(|| AppError::Unauthorized("缺少登录凭证".into()))?;
    let client_ip = resolve_client_ip(ip, &headers, state.cfg.server.trust_loopback_proxy);
    let (resp, new_refresh) =
        service::auth::refresh(&state.db, &state.cfg, &raw, Some(client_ip.to_string())).await?;
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

#[cfg(test)]
mod tests {
    use super::resolve_client_ip;
    use axum::http::{HeaderMap, HeaderValue};
    use std::net::{IpAddr, SocketAddr};

    fn peer(ip: &str) -> SocketAddr {
        SocketAddr::new(ip.parse().unwrap(), 12345)
    }

    fn forwarded(value: &str) -> HeaderMap {
        let mut headers = HeaderMap::new();
        headers.insert("x-forwarded-for", HeaderValue::from_str(value).unwrap());
        headers
    }

    #[test]
    fn forwarded_ip_is_ignored_by_default() {
        assert_eq!(
            resolve_client_ip(peer("127.0.0.1"), &forwarded("203.0.113.8"), false),
            IpAddr::from([127, 0, 0, 1])
        );
    }

    #[test]
    fn forwarded_ip_is_ignored_for_non_loopback_peer() {
        assert_eq!(
            resolve_client_ip(peer("192.0.2.10"), &forwarded("203.0.113.8"), true),
            IpAddr::from([192, 0, 2, 10])
        );
    }

    #[test]
    fn trusted_loopback_proxy_can_supply_one_canonical_ip() {
        assert_eq!(
            resolve_client_ip(peer("127.0.0.1"), &forwarded("203.0.113.8"), true),
            IpAddr::from([203, 0, 113, 8])
        );
    }

    #[test]
    fn forwarded_chains_and_noncanonical_values_are_rejected() {
        let loopback = peer("127.0.0.1");
        assert_eq!(
            resolve_client_ip(loopback, &forwarded("203.0.113.8, 198.51.100.4"), true),
            IpAddr::from([127, 0, 0, 1])
        );
        assert_eq!(
            resolve_client_ip(loopback, &forwarded(" 203.0.113.8"), true),
            IpAddr::from([127, 0, 0, 1])
        );
        assert_eq!(
            resolve_client_ip(loopback, &forwarded("not-an-ip"), true),
            IpAddr::from([127, 0, 0, 1])
        );
        let mut repeated = forwarded("203.0.113.8");
        repeated.append("x-forwarded-for", HeaderValue::from_static("198.51.100.4"));
        assert_eq!(
            resolve_client_ip(loopback, &repeated, true),
            IpAddr::from([127, 0, 0, 1])
        );
    }
}
