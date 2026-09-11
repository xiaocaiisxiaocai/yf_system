use jsonwebtoken::{decode, encode, DecodingKey, EncodingKey, Header, Validation};
use serde::{Deserialize, Serialize};

use crate::error::AppError;

#[derive(Debug, Serialize, Deserialize)]
pub struct Claims {
    /// employee number
    pub sub: String,
    pub uid: u64,
    /// Persistent refresh-session family identifier.
    pub sid: String,
    pub jti: String,
    pub iat: usize,
    pub exp: usize,
}

pub fn issue_access(
    secret: &str,
    uid: u64,
    employee_no: &str,
    session_id: &str,
    ttl_minutes: i64,
) -> Result<(String, usize), AppError> {
    let now = chrono::Utc::now();
    let exp = (now + chrono::Duration::minutes(ttl_minutes)).timestamp() as usize;
    let claims = Claims {
        sub: employee_no.to_string(),
        uid,
        sid: session_id.to_string(),
        jti: uuid::Uuid::new_v4().to_string(),
        iat: now.timestamp() as usize,
        exp,
    };
    let token = encode(
        &Header::default(),
        &claims,
        &EncodingKey::from_secret(secret.as_bytes()),
    )?;
    Ok((token, exp))
}

pub fn parse_access(secret: &str, token: &str) -> Result<Claims, AppError> {
    let data = decode::<Claims>(
        token,
        &DecodingKey::from_secret(secret.as_bytes()),
        &Validation::default(),
    )?;
    Ok(data.claims)
}
