use sea_orm::DatabaseConnection;
use std::collections::HashMap;
use std::sync::{Arc, Mutex};
use std::time::Instant;

use crate::config::Config;

/// 验证码内存存储：captcha_id → (答案, 过期时间)。单进程部署足够。
pub type CaptchaStore = Arc<Mutex<HashMap<String, (String, Instant)>>>;

/// 简单滑动窗口限流：key(IP/接口) → 近期请求时刻。单进程部署足够。
pub type RateStore = Arc<Mutex<HashMap<String, Vec<Instant>>>>;

#[derive(Clone)]
pub struct AppState {
    pub db: DatabaseConnection,
    pub cfg: Arc<Config>,
    pub captchas: CaptchaStore,
    pub rates: RateStore,
}

impl AppState {
    pub fn new(db: DatabaseConnection, cfg: Config) -> Self {
        Self {
            db,
            cfg: Arc::new(cfg),
            captchas: Arc::new(Mutex::new(HashMap::new())),
            rates: Arc::new(Mutex::new(HashMap::new())),
        }
    }

    /// 限流：window 内最多 max 次；超过返回 false。顺带清理过期记录防内存膨胀。
    pub fn rate_allow(&self, key: &str, max: usize, window: std::time::Duration) -> bool {
        let now = Instant::now();
        let mut map = match self.rates.lock() {
            Ok(m) => m,
            Err(_) => return true, // 锁损坏不阻断业务
        };
        // 定期全量清理（键数过多时）
        if map.len() > 10_000 {
            map.retain(|_, v| v.iter().any(|t| now.duration_since(*t) < window));
        }
        let entry = map.entry(key.to_string()).or_default();
        entry.retain(|t| now.duration_since(*t) < window);
        if entry.len() >= max {
            return false;
        }
        entry.push(now);
        true
    }
}
