//! 邮件通知入队（email_outbox）。收件人规则见《03-模块划分与接口规划》§4.10。
use chrono::Utc;
use sea_orm::sea_query::OnConflict;
use sea_orm::{ActiveModelTrait, ColumnTrait, ConnectionTrait, EntityTrait, QueryFilter, Set};

use crate::entity::enums::{CommonStatus, OutboxEventType, OutboxStatus, RoundStatus};
use crate::entity::{
    email_outbox, messages, project_members, projects, roles, system_configs, user_roles, users,
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

/// 收件人：项目内部成员 + 该供应商启用账号（排除操作者），按邮箱去重
async fn recipients(
    db: &impl ConnectionTrait,
    project: &projects::Model,
    exclude_user: u64,
) -> ApiResult<Vec<(u64, String)>> {
    let member_ids: Vec<u64> = project_members::Entity::find()
        .filter(project_members::Column::ProjectId.eq(project.id))
        .all(db)
        .await?
        .into_iter()
        .map(|m| m.user_id)
        .collect();
    let mut accounts = users::Entity::find()
        .filter(users::Column::Id.is_in(member_ids))
        .filter(users::Column::Status.eq(CommonStatus::Active))
        .all(db)
        .await?;
    let supplier_accounts = users::Entity::find()
        .filter(users::Column::SupplierId.eq(project.supplier_id))
        .filter(users::Column::Status.eq(CommonStatus::Active))
        .all(db)
        .await?;
    accounts.extend(supplier_accounts);

    let mut seen = std::collections::HashSet::new();
    let mut out = Vec::new();
    for u in accounts {
        if u.id == exclude_user || u.email.trim().is_empty() || !seen.insert(u.email.clone()) {
            continue;
        }
        out.push((u.id, u.email));
    }
    Ok(out)
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
    for (uid, email) in recipients(db, project, exclude_user).await? {
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

#[cfg(test)]
mod tests {
    use super::storage_warning_key;

    #[test]
    fn storage_warning_key_is_daily_and_per_recipient() {
        let date = chrono::NaiveDate::from_ymd_opt(2026, 9, 4).unwrap();
        assert_eq!(storage_warning_key(date, 7), "storage-warning:2026-09-04:7");
        assert_ne!(storage_warning_key(date, 7), storage_warning_key(date, 8));
    }
}
