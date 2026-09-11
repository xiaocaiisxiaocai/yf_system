use argon2::password_hash::{rand_core::OsRng, SaltString};
use argon2::{Argon2, PasswordHash, PasswordHasher, PasswordVerifier};

use crate::error::AppError;

pub use migration::password_policy::{strong_enough, MAX_PASSWORD_BYTES, POLICY_MESSAGE};

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

#[cfg(test)]
mod tests {
    #[test]
    fn passwords_enforce_length_and_common_weak_patterns() {
        assert!(super::strong_enough("Regression123"));
        assert!(super::strong_enough("这是一句可以记住的长密码短语"));
        let boundary = "😀abcdeFG".repeat(8);
        assert!(super::strong_enough(&boundary));
        assert!(!super::strong_enough(&(boundary + "x")));
        for weak in [
            "123456",
            "shortphrase",
            "Password123456!",
            "P@ssw0rd1234!",
            "passwordpassword1!",
            "qwerty123456",
            "123456789012",
            "abcdabcdabcd",
            "            ",
        ] {
            assert!(!super::strong_enough(weak));
        }
    }

    #[test]
    fn existing_short_password_hashes_still_verify() {
        let old = super::hash("Old123").unwrap();
        assert!(super::verify("Old123", &old));
        assert!(!super::strong_enough("Old123"));
    }
}
