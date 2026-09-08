use argon2::password_hash::{rand_core::OsRng, SaltString};
use argon2::{Argon2, PasswordHash, PasswordHasher, PasswordVerifier};

use crate::error::AppError;

pub const MAX_PASSWORD_BYTES: usize = 128;
pub const MIN_PASSWORD_CHARS: usize = 6;
pub const MAX_PASSWORD_CHARS: usize = 20;

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

/// 密码策略：6–20 个字符；登录入口另有 UTF-8 字节数上限，避免超大请求。
pub fn strong_enough(password: &str) -> bool {
    password.chars().count() >= MIN_PASSWORD_CHARS
        && password.chars().count() <= MAX_PASSWORD_CHARS
        && password.len() <= MAX_PASSWORD_BYTES
}

#[cfg(test)]
mod tests {
    #[test]
    fn passwords_follow_the_six_to_twenty_character_policy() {
        assert!(super::strong_enough("123456"));
        assert!(super::strong_enough("纯中文密码啊"));
        assert!(super::strong_enough(&"a".repeat(20)));
        assert!(!super::strong_enough("12345"));
        assert!(!super::strong_enough(&"a".repeat(21)));
    }
}
