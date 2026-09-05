//! 图形验证码：4 位随机字符，SVG 返回，5 分钟有效，一次性核销。
//! 存储于 AppState.captchas（单进程部署足够；多实例部署需换 Redis）。
use rand::Rng;
use std::time::{Duration, Instant};

use crate::state::CaptchaStore;

const TTL: Duration = Duration::from_secs(300);

pub fn issue(store: &CaptchaStore) -> (String, String) {
    const CHARS: &[u8] = b"ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    let mut rng = rand::thread_rng();
    let code: String = (0..4)
        .map(|_| CHARS[rng.gen_range(0..CHARS.len())] as char)
        .collect();
    let id = uuid::Uuid::new_v4().to_string();

    let mut map = store.lock().unwrap();
    map.retain(|_, (_, exp)| *exp > Instant::now());
    map.insert(id.clone(), (code.clone(), Instant::now() + TTL));
    drop(map);

    let svg = render_svg(&code);
    (id, svg)
}

/// 核销（一次性）
pub fn verify(store: &CaptchaStore, id: &str, code: &str) -> bool {
    let mut map = store.lock().unwrap();
    match map.remove(id) {
        Some((answer, exp)) => exp > Instant::now() && answer.eq_ignore_ascii_case(code.trim()),
        None => false,
    }
}

fn render_svg(code: &str) -> String {
    let mut rng = rand::thread_rng();
    let mut chars_svg = String::new();
    for (i, c) in code.chars().enumerate() {
        let x = 20 + i as i32 * 28;
        let y = rng.gen_range(28..40);
        let rot = rng.gen_range(-20..20);
        let color = rng.gen_range(0..360);
        chars_svg.push_str(&format!(
            r#"<text x="{}" y="{}" font-size="28" font-family="monospace" font-weight="bold" fill="hsl({},60%,40%)" transform="rotate({} {} {})">{}</text>"#,
            x, y, color, rot, x, y, c
        ));
    }
    let mut noise = String::new();
    for _ in 0..4 {
        noise.push_str(&format!(
            r#"<line x1="{}" y1="{}" x2="{}" y2="{}" stroke="hsl({},50%,70%)" stroke-width="1"/>"#,
            rng.gen_range(0..140),
            rng.gen_range(0..50),
            rng.gen_range(0..140),
            rng.gen_range(0..50),
            rng.gen_range(0..360)
        ));
    }
    format!(
        r##"<svg xmlns="http://www.w3.org/2000/svg" width="140" height="50" viewBox="0 0 140 50"><rect width="140" height="50" fill="#f7f8fa"/>{}{}</svg>"##,
        noise, chars_svg
    )
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn captcha_is_single_use_case_insensitive_and_expires() {
        let store = CaptchaStore::default();
        let (id, svg) = issue(&store);
        assert!(svg.contains("<svg"));
        let code = store.lock().unwrap()[&id].0.clone();
        assert!(verify(&store, &id, &format!(" {} ", code.to_lowercase())));
        assert!(!verify(&store, &id, &code));
        let (wrong, _) = issue(&store);
        assert!(!verify(&store, &wrong, "wrong"));
        assert!(!store.lock().unwrap().contains_key(&wrong));
        store.lock().unwrap().insert(
            "expired".into(),
            ("ABCD".into(), Instant::now() - Duration::from_secs(1)),
        );
        assert!(!verify(&store, "expired", "ABCD"));
    }
}
