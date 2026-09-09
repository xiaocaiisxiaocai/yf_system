//! Shared input contracts for internal and supplier accounts.
use crate::error::{ApiResult, AppError};

pub fn employee_no(value: &str) -> ApiResult<()> {
    let value = value.trim();
    if !(3..=32).contains(&value.len())
        || !value.chars().all(|c| c.is_ascii_alphanumeric() || c == '_')
    {
        return Err(AppError::BadRequest(
            "工号需为 3~32 位字母/数字/下划线".into(),
        ));
    }
    Ok(())
}

pub fn email(value: &str) -> ApiResult<()> {
    let value = value.trim();
    if value.chars().count() > 128 || value.parse::<lettre::Address>().is_err() {
        return Err(AppError::BadRequest("邮箱格式不正确或超过 128 字符".into()));
    }
    Ok(())
}
