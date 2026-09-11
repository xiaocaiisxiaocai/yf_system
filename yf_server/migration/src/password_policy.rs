//! Shared by bootstrap and all password-writing services; login accepts existing hashes.
pub const MAX_PASSWORD_BYTES: usize = 256;
pub const POLICY_MESSAGE: &str = "密码需 12-64 个字符，不能使用常见弱密码或简单重复序列";

pub fn strong_enough(password: &str) -> bool {
    static POLICY: std::sync::OnceLock<serde_json::Value> = std::sync::OnceLock::new();
    let policy = POLICY.get_or_init(|| {
        serde_json::from_str(include_str!("../../../web/src/utils/password-policy.json"))
            .expect("checked-in password policy must be valid")
    });
    let chars: Vec<_> = password.chars().collect();
    if chars.len() < policy["minChars"].as_u64().unwrap() as usize
        || chars.len() > policy["maxChars"].as_u64().unwrap() as usize
        || password.len() > MAX_PASSWORD_BYTES
    {
        return false;
    }
    let core = password.trim_end_matches(|c: char| c.is_ascii_digit() || c.is_ascii_punctuation());
    let stem: String = core
        .chars()
        .map(|c| match c {
            '@' | '4' => 'a',
            '$' | '5' => 's',
            '0' => 'o',
            '1' | '!' => 'i',
            '3' => 'e',
            '7' => 't',
            other => other.to_ascii_lowercase(),
        })
        .filter(char::is_ascii_alphabetic)
        .collect();
    if policy["blockedStems"]
        .as_array()
        .unwrap()
        .iter()
        .any(|word| {
            let word = word.as_str().unwrap();
            !stem.is_empty()
                && stem.len().is_multiple_of(word.len())
                && word.repeat(stem.len() / word.len()) == stem
        })
        || "0123456789".repeat(8).contains(password)
        || "9876543210".repeat(8).contains(password)
    {
        return false;
    }
    !(1..=4).any(|period| {
        chars
            .iter()
            .enumerate()
            .all(|(i, c)| *c == chars[i % period])
    })
}

#[cfg(test)]
mod tests {
    #[test]
    fn shared_policy_constants_and_bootstrap_rejection() {
        let policy: serde_json::Value =
            serde_json::from_str(include_str!("../../../web/src/utils/password-policy.json"))
                .unwrap();
        assert_eq!(
            policy["maxBytes"].as_u64(),
            Some(super::MAX_PASSWORD_BYTES as u64)
        );
        assert_eq!(policy["message"].as_str(), Some(super::POLICY_MESSAGE));
        assert!(!super::strong_enough("Password123456"));
        assert!(super::strong_enough("Random-Phrase-794"));
    }
}
