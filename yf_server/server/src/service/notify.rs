//! 邮件通知入队（email_outbox）。收件人规则见《03-模块划分与接口规划》§4.10。
use chrono::Utc;
use sea_orm::sea_query::OnConflict;
use sea_orm::{
    ActiveModelTrait, ColumnTrait, ConnectionTrait, EntityTrait, PaginatorTrait, QueryFilter,
    QueryOrder, QuerySelect, Set,
};
use serde_json::{json, Value};

use crate::entity::enums::{CommonStatus, OutboxEventType, OutboxStatus, RoundStatus};
use crate::entity::{
    audit_logs, email_outbox, messages, projects, roles, system_configs, user_roles, users,
};
use crate::error::ApiResult;
use crate::middleware::auth::CurrentUser;
use crate::state::AppState;

/// 系统参数 notify.enabled 总开关（默认开）；关闭时不再入队新通知
pub async fn enabled(db: &impl ConnectionTrait) -> bool {
    system_configs::Entity::find_by_id("notify.enabled".to_string())
        .one(db)
        .await
        .ok()
        .flatten()
        .and_then(|c| c.cfg_value)
        .map(|v| v.trim().eq_ignore_ascii_case("true") || v.trim() == "1")
        .unwrap_or(true)
}

/// 只向管理员展示可诊断的邮箱片段；不返回 SMTP 用户名、密码或完整收件地址。
pub(crate) fn mask_email(address: &str) -> String {
    let value = address.trim();
    let Some((local, domain)) = value.split_once('@') else {
        return if value.is_empty() {
            "未配置".to_string()
        } else {
            "***".to_string()
        };
    };
    let first = local.chars().next().unwrap_or('*');
    format!("{first}***@{domain}")
}

/// SMTP 库错误可能包含主机、账号、收件地址及服务端响应，只保留管理员可操作的错误类别。
pub(crate) fn sanitize_mail_error(message: &str) -> String {
    if matches!(
        message,
        "SMTP 认证失败"
            | "SMTP 连接超时"
            | "SMTP 连接失败"
            | "SMTP 发送失败"
            | "收件地址或发件地址无效"
    ) {
        return message.to_string();
    }
    let lower = message.to_ascii_lowercase();
    if message.contains("地址") || lower.contains("address") || lower.contains("mailbox") {
        "收件地址或发件地址无效".to_string()
    } else if message.contains("认证失败") || lower.contains("authentication failed") {
        "SMTP 认证失败".to_string()
    } else if message.contains("连接超时")
        || lower.contains("timeout")
        || lower.contains("timed out")
    {
        "SMTP 连接超时".to_string()
    } else if lower.contains("auth")
        || lower.contains("credential")
        || lower.contains("login")
        || lower.contains("password")
    {
        "SMTP 认证失败".to_string()
    } else if lower.contains("connection")
        || lower.contains("connect")
        || lower.contains("dns")
        || lower.contains("network")
        || lower.contains("tls")
    {
        "SMTP 连接失败".to_string()
    } else {
        "SMTP 发送失败".to_string()
    }
}

pub(crate) fn event_type_name(event: OutboxEventType) -> &'static str {
    match event {
        OutboxEventType::StorageWarning => "STORAGE_WARNING",
        OutboxEventType::FileUploaded => "FILE_UPLOADED",
        OutboxEventType::MessageCreated => "MESSAGE_CREATED",
        OutboxEventType::RoundConfirmed => "ROUND_CONFIRMED",
        OutboxEventType::RoundRejected => "ROUND_REJECTED",
        OutboxEventType::RoundCancelled => "ROUND_CANCELLED",
    }
}

struct MissingEmailAccount {
    id: u64,
    employee_no: String,
    real_name: String,
}

struct RecipientSelection {
    recipients: Vec<(u64, String)>,
    missing_email: Vec<MissingEmailAccount>,
}

async fn audit_missing_email(
    db: &impl ConnectionTrait,
    event: OutboxEventType,
    account: MissingEmailAccount,
) {
    crate::service::audit::log(
        db,
        None,
        None,
        "EMAIL_SKIPPED_MISSING_EMAIL",
        Some("user"),
        Some(account.id.to_string()),
        Some(json!({
            "eventType": event_type_name(event),
            "reason": "RECIPIENT_EMAIL_MISSING",
            "employeeNo": account.employee_no,
            "realName": account.real_name,
        })),
        None,
    )
    .await;
}

/// 收件人：启用创建人、内部成员和供应商账号，排除操作者并按邮箱去重。
async fn recipients(
    db: &impl ConnectionTrait,
    project: &projects::Model,
    exclude_user: u64,
) -> ApiResult<RecipientSelection> {
    let accounts = super::participants::accounts(db, project).await?;

    let mut seen = std::collections::HashSet::new();
    let mut out = Vec::new();
    let mut missing_email = Vec::new();
    for u in accounts {
        if u.id == exclude_user {
            continue;
        }
        if u.email.trim().is_empty() {
            missing_email.push(MissingEmailAccount {
                id: u.id,
                employee_no: u.employee_no,
                real_name: u.real_name,
            });
            continue;
        }
        if !seen.insert(u.email.clone()) {
            continue;
        }
        out.push((u.id, u.email));
    }
    Ok(RecipientSelection {
        recipients: out,
        missing_email,
    })
}

async fn enqueue(
    db: &impl ConnectionTrait,
    event: OutboxEventType,
    project: &projects::Model,
    round_id: Option<u64>,
    subject: String,
    body: String,
    exclude_user: u64,
) -> ApiResult<()> {
    if !enabled(db).await {
        return Ok(());
    }
    let selection = recipients(db, project, exclude_user).await?;
    for account in selection.missing_email {
        audit_missing_email(db, event, account).await;
    }
    for (uid, email) in selection.recipients {
        email_outbox::ActiveModel {
            event_type: Set(event),
            project_id: Set(Some(project.id)),
            dedupe_key: Set(None),
            round_id: Set(round_id),
            recipient_user_id: Set(Some(uid)),
            recipient_email: Set(email),
            subject: Set(subject.clone()),
            body: Set(body.clone()),
            status: Set(OutboxStatus::Pending),
            retry_count: Set(0),
            next_attempt_at: Set(None),
            last_error: Set(None),
            sent_at: Set(None),
            created_at: Set(Utc::now()),
            ..Default::default()
        }
        .insert(db)
        .await?;
    }
    Ok(())
}

fn storage_warning_key(date: chrono::NaiveDate, user_id: u64) -> String {
    format!("storage-warning:{date}:{user_id}")
}

/// 磁盘使用率达到阈值时，向所有启用的系统管理员每天最多入队一封告警。
/// 唯一幂等键同时防止多个服务实例重复入队。
pub async fn enqueue_storage_warning(state: &AppState) -> ApiResult<()> {
    if !enabled(&state.db).await {
        return Ok(());
    }
    let snapshot = super::config::storage_snapshot(state).await?;
    if !snapshot.warning {
        return Ok(());
    }

    let Some(admin_role) = roles::Entity::find()
        .filter(roles::Column::IsBuiltIn.eq(true))
        .filter(roles::Column::Name.eq("系统管理员"))
        .filter(roles::Column::Status.eq(CommonStatus::Active))
        .one(&state.db)
        .await?
    else {
        return Ok(());
    };
    let admin_ids = user_roles::Entity::find()
        .filter(user_roles::Column::RoleId.eq(admin_role.id))
        .all(&state.db)
        .await?
        .into_iter()
        .map(|binding| binding.user_id)
        .collect::<Vec<_>>();
    if admin_ids.is_empty() {
        return Ok(());
    }
    let recipients = users::Entity::find()
        .filter(users::Column::Id.is_in(admin_ids))
        .filter(users::Column::Status.eq(CommonStatus::Active))
        .all(&state.db)
        .await?;

    let date = Utc::now().date_naive();
    let subject = format!(
        "[协作平台] 存储空间告警：已使用 {:.1}%",
        snapshot.used_percent
    );
    let body = format!(
        "存储目录：{}\n所在卷：{}\n已使用：{:.1}%\n告警阈值：{:.1}%\n可用空间：{} 字节\n\n请及时扩容或按数据保留策略清理文件。\n\n（本邮件由系统自动发送，每位管理员每天最多一封）",
        snapshot.root,
        snapshot.mount_point,
        snapshot.used_percent,
        snapshot.warn_percent,
        snapshot.available_bytes,
    );
    for user in recipients {
        if user.email.trim().is_empty() {
            audit_missing_email(
                &state.db,
                OutboxEventType::StorageWarning,
                MissingEmailAccount {
                    id: user.id,
                    employee_no: user.employee_no,
                    real_name: user.real_name,
                },
            )
            .await;
            continue;
        }
        let model = email_outbox::ActiveModel {
            event_type: Set(OutboxEventType::StorageWarning),
            dedupe_key: Set(Some(storage_warning_key(date, user.id))),
            project_id: Set(None),
            round_id: Set(None),
            recipient_user_id: Set(Some(user.id)),
            recipient_email: Set(user.email),
            subject: Set(subject.clone()),
            body: Set(body.clone()),
            status: Set(OutboxStatus::Pending),
            retry_count: Set(0),
            next_attempt_at: Set(None),
            last_error: Set(None),
            sent_at: Set(None),
            created_at: Set(Utc::now()),
            ..Default::default()
        };
        email_outbox::Entity::insert(model)
            // MySQL 5.7 不支持 SeaORM 生成的 DO NOTHING 形式；重复时把唯一键更新为自身，实现无副作用幂等。
            .on_conflict(
                OnConflict::column(email_outbox::Column::DedupeKey)
                    .update_column(email_outbox::Column::DedupeKey)
                    .to_owned(),
            )
            .exec_without_returning(&state.db)
            .await?;
    }
    Ok(())
}

/// 新文件上传通知
pub async fn enqueue_file_notice(
    db: &impl ConnectionTrait,
    project: &projects::Model,
    round_no: i32,
    file_name: &str,
    uploader: &CurrentUser,
    base_url: &str,
) -> ApiResult<()> {
    let subject = format!("[协作平台] 项目「{}」有新文件上传", project.name);
    let body = format!(
        "项目：{}\n轮次：第 {} 轮\n文件：{}\n上传人：工号 {}\n\n请登录平台查看并下载：{}\n\n（本邮件由系统自动发送，附件请登录平台获取）",
        project.name, round_no, file_name, uploader.employee_no, base_url
    );
    enqueue(
        db,
        OutboxEventType::FileUploaded,
        project,
        None,
        subject,
        body,
        uploader.id,
    )
    .await
}

/// 新留言通知
pub async fn enqueue_message_notice(
    db: &impl ConnectionTrait,
    project: &projects::Model,
    message: &messages::Model,
    sender: &CurrentUser,
    base_url: &str,
) -> ApiResult<()> {
    let preview: String = message.content.chars().take(80).collect();
    let subject = format!("[协作平台] 项目「{}」有新留言", project.name);
    let body =
        format!(
        "项目：{}\n留言人：工号 {}\n内容：{}{}\n\n请登录平台查看：{}\n\n（本邮件由系统自动发送）",
        project.name,
        sender.employee_no,
        preview,
        if message.content.chars().count() > 80 { "…" } else { "" },
        base_url
    );
    enqueue(
        db,
        OutboxEventType::MessageCreated,
        project,
        message.round_id,
        subject,
        body,
        sender.id,
    )
    .await
}

/// 供上传完成调用：需要项目模型 + 轮次号
pub async fn enqueue_file_upload(
    db: &impl ConnectionTrait,
    project: &projects::Model,
    round_no: i32,
    file_name: &str,
    uploader: &CurrentUser,
    base_url: &str,
) -> ApiResult<()> {
    enqueue_file_notice(db, project, round_no, file_name, uploader, base_url).await
}

pub struct RoundNotice<'a> {
    pub round_id: u64,
    pub round_no: i32,
    pub to: RoundStatus,
    pub reason: Option<&'a str>,
    pub operator: &'a CurrentUser,
    pub base_url: &'a str,
}

/// 轮次流转通知（确认/驳回/撤销）
pub async fn enqueue_round_notice(
    db: &impl ConnectionTrait,
    project: &projects::Model,
    notice: RoundNotice<'_>,
) -> ApiResult<()> {
    let RoundNotice {
        round_id,
        round_no,
        to,
        reason,
        operator,
        base_url,
    } = notice;
    let (event, action) = match to {
        RoundStatus::Confirmed => (OutboxEventType::RoundConfirmed, "已确认"),
        RoundStatus::Rejected => (OutboxEventType::RoundRejected, "已驳回"),
        RoundStatus::Cancelled => (OutboxEventType::RoundCancelled, "已撤销"),
        _ => return Ok(()),
    };
    let subject = format!(
        "[协作平台] 项目「{}」第 {} 轮{}",
        project.name, round_no, action
    );
    let reason_line = reason
        .map(|r| format!("\n驳回原因：{r}"))
        .unwrap_or_default();
    let body = format!(
        "项目：{}\n轮次：第 {} 轮\n结果：{}\n操作人：工号 {}{}\n\n请登录平台查看：{}\n\n（本邮件由系统自动发送）",
        project.name, round_no, action, operator.employee_no, reason_line, base_url
    );
    enqueue(
        db,
        event,
        project,
        Some(round_id),
        subject,
        body,
        operator.id,
    )
    .await
}

async fn outbox_count(db: &impl ConnectionTrait, status: OutboxStatus) -> ApiResult<u64> {
    Ok(email_outbox::Entity::find()
        .filter(email_outbox::Column::Status.eq(status))
        .count(db)
        .await?)
}

fn safe_mail_detail(detail: Option<Value>) -> Value {
    let Some(object) = detail.and_then(|value| value.as_object().cloned()) else {
        return json!({});
    };
    let mut safe = serde_json::Map::new();
    for key in [
        "eventType",
        "status",
        "reason",
        "employeeNo",
        "realName",
        "retryCount",
        "userId",
    ] {
        if let Some(value) = object.get(key) {
            safe.insert(key.to_string(), value.clone());
        }
    }
    if let Some(recipient) = object.get("recipient").and_then(Value::as_str) {
        safe.insert(
            "recipient".to_string(),
            Value::String(mask_email(recipient)),
        );
    }
    if let Some(error) = object.get("error").and_then(Value::as_str) {
        safe.insert(
            "error".to_string(),
            Value::String(sanitize_mail_error(error)),
        );
    }
    Value::Object(safe)
}

async fn latest_audit_time(
    db: &impl ConnectionTrait,
    action: &str,
) -> ApiResult<Option<chrono::DateTime<Utc>>> {
    Ok(audit_logs::Entity::find()
        .filter(audit_logs::Column::Action.eq(action))
        .order_by_desc(audit_logs::Column::CreatedAt)
        .order_by_desc(audit_logs::Column::Id)
        .one(db)
        .await?
        .map(|row| row.created_at))
}

async fn latest_sent_time(db: &impl ConnectionTrait) -> ApiResult<Option<chrono::DateTime<Utc>>> {
    Ok(email_outbox::Entity::find()
        .filter(email_outbox::Column::Status.eq(OutboxStatus::Sent))
        .filter(email_outbox::Column::SentAt.is_not_null())
        .order_by_desc(email_outbox::Column::SentAt)
        .order_by_desc(email_outbox::Column::Id)
        .one(db)
        .await?
        .and_then(|row| row.sent_at))
}

/// 管理员邮件诊断快照：配置、队列、最近结果与未填邮箱账号均来自当前数据库状态。
pub async fn mail_status(state: &AppState) -> ApiResult<Value> {
    let smtp = &state.cfg.smtp;
    let configured = !smtp.host.trim().is_empty()
        && !smtp.username.trim().is_empty()
        && !smtp.password.is_empty()
        && !smtp.from.trim().is_empty();
    let notifications_enabled = enabled(&state.db).await;

    let pending = outbox_count(&state.db, OutboxStatus::Pending).await?;
    let sending = outbox_count(&state.db, OutboxStatus::Sending).await?;
    let sent = outbox_count(&state.db, OutboxStatus::Sent).await?;
    let failed = outbox_count(&state.db, OutboxStatus::Failed).await?;

    let active_users = users::Entity::find()
        .filter(users::Column::Status.eq(CommonStatus::Active))
        .order_by_asc(users::Column::EmployeeNo)
        .all(&state.db)
        .await?;
    let missing_email_accounts = active_users
        .iter()
        .filter(|user| user.email.trim().is_empty())
        .take(20)
        .map(|user| {
            json!({
                "userId": user.id,
                "employeeNo": user.employee_no,
                "realName": user.real_name,
                "userType": user.user_type.as_str(),
                "status": "ACTIVE",
            })
        })
        .collect::<Vec<_>>();
    let missing_email_count = active_users
        .iter()
        .filter(|user| user.email.trim().is_empty())
        .count();

    let mail_actions = [
        "EMAIL_SENT",
        "EMAIL_FAILED",
        "EMAIL_RETRY",
        "EMAIL_SKIPPED_MISSING_EMAIL",
    ];
    let recent = audit_logs::Entity::find()
        .filter(audit_logs::Column::Action.is_in(mail_actions.iter().copied()))
        .order_by_desc(audit_logs::Column::CreatedAt)
        .order_by_desc(audit_logs::Column::Id)
        .limit(10)
        .all(&state.db)
        .await?
        .into_iter()
        .map(|row| {
            json!({
                "id": row.id,
                "action": row.action,
                "targetType": row.target_type,
                "targetId": row.target_id,
                "detail": safe_mail_detail(row.detail),
                "createdAt": row.created_at,
            })
        })
        .collect::<Vec<_>>();

    Ok(json!({
        "configured": configured,
        "host": if configured { Value::String(smtp.host.clone()) } else { Value::Null },
        "port": if configured { Value::from(smtp.port) } else { Value::Null },
        "from": if configured { Value::String(mask_email(&smtp.from)) } else { Value::Null },
        "notificationsEnabled": notifications_enabled,
        "queue": {
            "pending": pending,
            "sending": sending,
            "sent": sent,
            "failed": failed,
        },
        "latestSentAt": latest_sent_time(&state.db).await?,
        "latestFailedAt": latest_audit_time(&state.db, "EMAIL_FAILED").await?,
        "missingEmailCount": missing_email_count,
        "missingEmailAccounts": missing_email_accounts,
        "recent": recent,
    }))
}

#[cfg(test)]
mod tests {
    use super::{mask_email, sanitize_mail_error, storage_warning_key};

    #[test]
    fn storage_warning_key_is_daily_and_per_recipient() {
        let date = chrono::NaiveDate::from_ymd_opt(2026, 9, 4).unwrap();
        assert_eq!(storage_warning_key(date, 7), "storage-warning:2026-09-04:7");
        assert_ne!(storage_warning_key(date, 7), storage_warning_key(date, 8));
    }

    #[test]
    fn mail_diagnostics_mask_addresses_and_sanitize_smtp_errors() {
        assert_eq!(mask_email("alice@example.com"), "a***@example.com");
        assert_eq!(mask_email(""), "未配置");
        let error =
            sanitize_mail_error("authentication failed for alice@example.com at smtp.secret.local");
        assert_eq!(error, "SMTP 认证失败");
        assert_eq!(sanitize_mail_error(&error), "SMTP 认证失败");
        assert_eq!(sanitize_mail_error("SMTP 连接失败"), "SMTP 连接失败");
        assert_eq!(sanitize_mail_error("SMTP 发送失败"), "SMTP 发送失败");
        assert!(!error.contains("alice@example.com"));
        assert!(!error.contains("smtp.secret.local"));
    }
}
