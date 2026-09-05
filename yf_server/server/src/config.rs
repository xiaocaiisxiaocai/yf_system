use serde::Deserialize;

#[derive(Debug, Clone, Deserialize)]
pub struct Config {
    pub server: ServerCfg,
    pub database: DbCfg,
    pub storage: StorageCfg,
    pub jwt: JwtCfg,
    pub upload: UploadCfg,
    pub smtp: SmtpCfg,
    pub web: WebCfg,
}

#[derive(Debug, Clone, Deserialize)]
pub struct ServerCfg {
    pub addr: String,
}

#[derive(Debug, Clone, Deserialize)]
pub struct DbCfg {
    pub url: String,
    #[serde(default = "default_true")]
    pub auto_migrate: bool,
}

#[derive(Debug, Clone, Deserialize)]
pub struct StorageCfg {
    pub root: String,
}

#[derive(Debug, Clone, Deserialize)]
pub struct JwtCfg {
    pub secret: String,
    pub access_ttl_minutes: i64,
    pub refresh_ttl_days: i64,
    #[serde(default)]
    pub cookie_secure: bool,
}

#[derive(Debug, Clone, Deserialize)]
pub struct UploadCfg {
    pub max_file_size: u64,
    pub chunk_size: u32,
}

#[derive(Debug, Clone, Deserialize)]
pub struct SmtpCfg {
    #[serde(default)]
    pub host: String,
    #[serde(default = "default_smtp_port")]
    pub port: u16,
    #[serde(default)]
    pub username: String,
    #[serde(default)]
    pub password: String,
    #[serde(default)]
    pub from: String,
}

#[derive(Debug, Clone, Deserialize)]
pub struct WebCfg {
    pub base_url: String,
}

fn default_true() -> bool {
    true
}
fn default_smtp_port() -> u16 {
    465
}

impl Config {
    pub fn load() -> Result<Self, Box<dyn std::error::Error>> {
        let path = std::env::var("YF_CONFIG").unwrap_or_else(|_| {
            if std::path::Path::new("config.local.toml").is_file() {
                "config.local.toml".to_string()
            } else {
                "config.toml".to_string()
            }
        });
        let raw = std::fs::read_to_string(&path)
            .map_err(|e| format!("无法读取配置文件 {path}: {e}（工作目录应为 yf_server/）"))?;
        let mut cfg: Config = toml::from_str(&raw)?;
        if let Ok(url) = std::env::var("YF_DATABASE_URL") {
            cfg.database.url = url;
        }
        if let Ok(s) = std::env::var("YF_JWT_SECRET") {
            cfg.jwt.secret = s;
        }
        if let Ok(host) = std::env::var("YF_SMTP_HOST") {
            cfg.smtp.host = host;
        }
        if let Ok(port) = std::env::var("YF_SMTP_PORT") {
            cfg.smtp.port = port
                .parse()
                .map_err(|_| "YF_SMTP_PORT 必须是 1-65535 的端口号")?;
        }
        if let Ok(username) = std::env::var("YF_SMTP_USERNAME") {
            cfg.smtp.username = username;
        }
        if let Ok(password) = std::env::var("YF_SMTP_PASSWORD") {
            cfg.smtp.password = password;
        }
        if let Ok(from) = std::env::var("YF_SMTP_FROM") {
            cfg.smtp.from = from;
        }
        if let Ok(base_url) = std::env::var("YF_WEB_BASE_URL") {
            cfg.web.base_url = base_url;
        }
        if cfg.database.url.trim().is_empty() {
            return Err(
                "database.url 不能为空；请使用 config.local.toml 或 YF_DATABASE_URL".into(),
            );
        }
        if cfg.jwt.secret.len() < 32 {
            return Err(
                "jwt.secret 至少需要 32 个字符；请使用 config.local.toml 或 YF_JWT_SECRET".into(),
            );
        }
        if !cfg.smtp.host.trim().is_empty()
            && (cfg.smtp.username.trim().is_empty()
                || cfg.smtp.password.is_empty()
                || cfg.smtp.from.trim().is_empty())
        {
            return Err("启用 SMTP 时 username、password、from 均不能为空".into());
        }
        if !(cfg.web.base_url.starts_with("http://") || cfg.web.base_url.starts_with("https://")) {
            return Err("web.base_url 必须是 http:// 或 https:// 开头的前端地址".into());
        }
        Ok(cfg)
    }
}
