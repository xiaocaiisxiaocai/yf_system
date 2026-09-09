//! 操作日志。关键业务使用 `insert` 放进同一事务；仅登录失败等无业务事务场景使用 best-effort `log`。
use sea_orm::{ActiveModelTrait, ConnectionTrait, DatabaseConnection, Set};

use crate::entity::audit_logs;
use crate::error::ApiResult;

#[allow(clippy::too_many_arguments)]
pub async fn insert(
    db: &impl ConnectionTrait,
    user_id: Option<u64>,
    employee_no: Option<String>,
    action: &str,
    target_type: Option<&str>,
    target_id: Option<String>,
    detail: Option<serde_json::Value>,
    ip: Option<String>,
) -> ApiResult<()> {
    let model = audit_logs::ActiveModel {
        user_id: Set(user_id),
        employee_no: Set(employee_no),
        action: Set(action.to_string()),
        target_type: Set(target_type.map(str::to_string)),
        target_id: Set(target_id),
        detail: Set(detail),
        ip: Set(ip),
        created_at: Set(chrono::Utc::now()),
        ..Default::default()
    }
    .insert(db)
    .await?;
    super::project_activity::capture_audit(db, &model).await?;
    Ok(())
}

#[allow(clippy::too_many_arguments)]
pub async fn log(
    db: &DatabaseConnection,
    user_id: Option<u64>,
    employee_no: Option<String>,
    action: &str,
    target_type: Option<&str>,
    target_id: Option<String>,
    detail: Option<serde_json::Value>,
    ip: Option<String>,
) {
    if let Err(e) = insert(
        db,
        user_id,
        employee_no,
        action,
        target_type,
        target_id,
        detail,
        ip,
    )
    .await
    {
        tracing::warn!(error = ?e, action, "审计日志写入失败");
    }
}
