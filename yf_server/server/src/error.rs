use axum::{
    http::StatusCode,
    response::{IntoResponse, Response},
    Json,
};

/// 业务错误：HTTP 状态码 + 业务码（段位约定见 03-模块划分与接口规划.md §3）
#[derive(Debug, thiserror::Error)]
pub enum AppError {
    #[error("{0}")]
    BadRequest(String),
    #[error("{0}")]
    Unauthorized(String),
    #[error("登录状态已过期")]
    TokenExpired,
    #[error("无操作权限")]
    Forbidden,
    #[error("请先修改初始密码")]
    MustChangePassword,
    #[error("无权访问该数据")]
    OutOfScope,
    #[error("资源不存在")]
    NotFound,
    #[error("{0}")]
    Conflict(String),
    #[error("失败次数过多，账号已锁定 30 分钟")]
    Locked,
    #[error("需要图形验证码")]
    CaptchaRequired,
    #[error("服务器内部错误")]
    Internal(String),
}

pub type ApiResult<T> = Result<T, AppError>;

impl AppError {
    fn status(&self) -> StatusCode {
        match self {
            Self::BadRequest(_) => StatusCode::BAD_REQUEST,
            Self::Unauthorized(_) | Self::TokenExpired => StatusCode::UNAUTHORIZED,
            Self::Forbidden | Self::OutOfScope | Self::MustChangePassword => StatusCode::FORBIDDEN,
            Self::NotFound => StatusCode::NOT_FOUND,
            Self::Conflict(_) => StatusCode::CONFLICT,
            Self::Locked => StatusCode::LOCKED,
            Self::CaptchaRequired => StatusCode::PRECONDITION_REQUIRED,
            Self::Internal(_) => StatusCode::INTERNAL_SERVER_ERROR,
        }
    }

    fn biz_code(&self) -> u32 {
        match self {
            Self::BadRequest(_) => 40001,
            Self::Unauthorized(_) => 40101,
            Self::TokenExpired => 40102,
            Self::Forbidden => 40301,
            Self::OutOfScope => 40302,
            Self::MustChangePassword => 40303,
            Self::NotFound => 40401,
            Self::Conflict(_) => 40901,
            Self::Locked => 42301,
            Self::CaptchaRequired => 42801,
            Self::Internal(_) => 50000,
        }
    }
}

impl IntoResponse for AppError {
    fn into_response(self) -> Response {
        let body = Json(serde_json::json!({
            "code": self.biz_code(),
            "message": self.to_string(),
        }));
        (self.status(), body).into_response()
    }
}

impl From<sea_orm::DbErr> for AppError {
    fn from(e: sea_orm::DbErr) -> Self {
        tracing::error!(error = ?e, "数据库错误");
        // 不回传 DbErr 原文——其中含 SQL/表名/约束名等内部信息
        Self::Internal("数据库操作失败".into())
    }
}

impl From<jsonwebtoken::errors::Error> for AppError {
    fn from(e: jsonwebtoken::errors::Error) -> Self {
        use jsonwebtoken::errors::ErrorKind;
        match e.kind() {
            ErrorKind::ExpiredSignature => Self::TokenExpired,
            _ => Self::Unauthorized("登录状态无效".into()),
        }
    }
}
