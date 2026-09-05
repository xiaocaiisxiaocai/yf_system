use argon2::password_hash::{rand_core::OsRng, SaltString};
use argon2::{Argon2, PasswordHash, PasswordHasher, PasswordVerifier};

use crate::error::AppError;

pub const MAX_PASSWORD_BYTES: usize = 128;

pub fn hash(password: &str) -> Result<String, AppError> {
    let salt = SaltString::generate(&mut OsRng);
    Argon2::default()
        .hash_password(password.as_bytes(), &salt)
        .map(|h| h.to_string())
        .map_err(|e| AppError::Internal(format!("密码散列失败: {e}")))
}

pub fn verify(password: &str, hash: &str) -> bool {
    match PasswordHash::new(hash) {
        Ok(parsed) => Argon2::default()
            .verify_password(password.as_bytes(), &parsed)
            .is_ok(),
        Err(_) => false,
    }
}

/// 密码策略：≥8 位且同时包含字母与数字；UTF-8 字节数与登录入口上限一致。
pub fn strong_enough(password: &str) -> bool {
    password.chars().count() >= 8
        && password.len() <= MAX_PASSWORD_BYTES
        && password.chars().any(|c| c.is_ascii_alphabetic())
        && password.chars().any(|c| c.is_ascii_digit())
}

#[cfg(test)]
mod tests {
    #[test]
    fn passwords_must_fit_the_login_byte_limit() {
        assert!(super::strong_enough(&format!("A1{}", "a".repeat(126))));
        assert!(!super::strong_enough(&format!("A1{}", "a".repeat(127))));
        assert!(!super::strong_enough(&format!("A1{}", "中".repeat(43))));
    }
}
