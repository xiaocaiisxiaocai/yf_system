//! Project business timeline with stable cursor pagination.
use std::collections::HashSet;

use chrono::{DateTime, Datelike, Utc};
use sea_orm::{
    ActiveModelTrait, ColumnTrait, Condition, ConnectionTrait, DatabaseConnection, EntityTrait,
    QueryFilter, QueryOrder, QuerySelect, Set,
};
use serde::Deserialize;
use serde_json::{json, Value};

use crate::entity::enums::{FileStatus, MessageStatus};
use crate::entity::{audit_logs, files, messages, project_activities, projects, users};
use crate::error::{ApiResult, AppError};
use crate::middleware::auth::CurrentUser;

use super::scope;

const DEFAULT_PAGE_SIZE: u64 = 20;
const MAX_PAGE_SIZE: u64 = 50;

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ActivityQuery {
    #[serde(rename = "type")]
    pub activity_type: Option<String>,
    pub cursor: Option<String>,
    #[serde(default = "default_page_size")]
    pub page_size: u64,
}

fn default_page_size() -> u64 {
    DEFAULT_PAGE_SIZE
}

fn truncate(value: &str) -> Option<String> {
    let value = value.trim();
    (!value.is_empty()).then(|| value.chars().take(160).collect())
}

async fn actor_name(
    db: &impl ConnectionTrait,
    user_id: Option<u64>,
    employee_no: Option<&str>,
) -> Result<String, sea_orm::DbErr> {
    if let Some(user) = match user_id {
        Some(id) => users::Entity::find_by_id(id).one(db).await?,
        None => None,
    } {
        if !user.real_name.trim().is_empty() {
            return Ok(user.real_name);
        }
        if !user.employee_no.trim().is_empty() {
            return Ok(user.employee_no);
        }
    }
    Ok(employee_no
        .filter(|value| !value.trim().is_empty())
        .unwrap_or("未知")
        .to_owned())
}

fn workflow_activity(
    action: &str,
    detail: Option<&Value>,
) -> Option<(&'static str, &'static str, Option<String>)> {
    let (activity_action, title) = match action {
        "PROJECT_START" => ("START", "开始项目"),
        "PROJECT_RESTART" => ("RESTART", "重新开始项目"),
        "PROJECT_SUBMIT" => ("SUBMIT", "提交项目验收"),
        "PROJECT_CONFIRM" => ("CONFIRM", "确认项目完成"),
        "PROJECT_REJECT" => ("REJECT", "驳回项目验收"),
        "PROJECT_WITHDRAW" => ("WITHDRAW", "撤回项目验收"),
        "PROJECT_TERMINATE" => ("TERMINATE", "终止项目"),
        _ => return None,
    };
    let summary = detail
        .and_then(|detail| detail.get("reason"))
        .and_then(Value::as_str)
        .and_then(truncate);
    Some((activity_action, title, summary))
}

struct NewActivity {
    project_id: u64,
    activity_type: &'static str,
    action: String,
    title: String,
    summary: Option<String>,
    occurred_at: DateTime<Utc>,
    target_id: Option<u64>,
    source_key: String,
}

/// Mirror selected business audit events into the project timeline in the caller transaction.
pub async fn capture_audit(db: &impl ConnectionTrait, audit: &audit_logs::Model) -> ApiResult<()> {
    let target_id = match audit.target_id.as_deref() {
        Some(value) => match value.parse::<u64>() {
            Ok(id) if id.to_string() == value => id,
            _ => return Ok(()),
        },
        None => return Ok(()),
    };
    let target_type = if audit.action.starts_with("PROJECT_") {
        "project"
    } else if audit.action.starts_with("FILE_") {
        "file"
    } else if audit.action.starts_with("MESSAGE_") {
        "message"
    } else {
        return Ok(());
    };
    if audit.target_type.as_deref() != Some(target_type) {
        return Ok(());
    }

    let event = match audit.action.as_str() {
        "PROJECT_CREATE" | "PROJECT_UPDATE" | "PROJECT_MEMBERS" | "PROJECT_START"
        | "PROJECT_SUBMIT" | "PROJECT_CONFIRM" | "PROJECT_REJECT" | "PROJECT_WITHDRAW"
        | "PROJECT_TERMINATE" | "PROJECT_RESTART" => {
            let Some(project) = projects::Entity::find_by_id(target_id).one(db).await? else {
                return Ok(());
            };
            let (action, title, summary) = match audit.action.as_str() {
                "PROJECT_CREATE" => ("CREATE", "创建项目", truncate(&project.name)),
                "PROJECT_UPDATE" => ("UPDATE", "编辑项目", truncate(&project.name)),
                "PROJECT_MEMBERS" => ("MEMBERS_CHANGE", "调整项目成员", None),
                _ => {
                    let Some(workflow) =
                        workflow_activity(audit.action.as_str(), audit.detail.as_ref())
                    else {
                        return Ok(());
                    };
                    workflow
                }
            };
            NewActivity {
                project_id: project.id,
                activity_type: "PROJECT",
                action: action.to_owned(),
                title: title.to_owned(),
                summary,
                occurred_at: if action == "CREATE" {
                    project.created_at
                } else {
                    audit.created_at
                },
                target_id: Some(project.id),
                source_key: if action == "CREATE" {
                    format!("project:{}:create", project.id)
                } else if let Some(status_log_id) = audit
                    .detail
                    .as_ref()
                    .and_then(|detail| detail.get("statusLogId"))
                    .and_then(Value::as_u64)
                {
                    format!("project-status-log:{status_log_id}")
                } else {
                    format!("audit:{}", audit.id)
                },
            }
        }
        "FILE_UPLOAD" | "FILE_DELETE" => {
            let Some(file) = files::Entity::find_by_id(target_id).one(db).await? else {
                return Ok(());
            };
            let upload = audit.action == "FILE_UPLOAD";
            NewActivity {
                project_id: file.project_id,
                activity_type: "FILE",
                action: if upload { "UPLOAD" } else { "DELETE" }.to_owned(),
                title: if upload {
                    "上传文件"
                } else {
                    "删除文件"
                }
                .to_owned(),
                summary: truncate(&file.original_name),
                occurred_at: if upload {
                    file.created_at
                } else {
                    file.deleted_at.unwrap_or(audit.created_at)
                },
                target_id: Some(file.id),
                source_key: format!(
                    "file:{}:{}",
                    file.id,
                    if upload { "upload" } else { "delete" }
                ),
            }
        }
        "MESSAGE_CREATE" | "MESSAGE_DELETE" => {
            let Some(message) = messages::Entity::find_by_id(target_id).one(db).await? else {
                return Ok(());
            };
            let create = audit.action == "MESSAGE_CREATE";
            NewActivity {
                project_id: message.project_id,
                activity_type: "MESSAGE",
                action: if create { "CREATE" } else { "DELETE" }.to_owned(),
                title: if create {
                    "发表留言"
                } else {
                    "删除留言"
                }
                .to_owned(),
                summary: if create {
                    truncate(&message.content)
                } else {
                    None
                },
                occurred_at: if create {
                    message.created_at
                } else {
                    message.deleted_at.unwrap_or(audit.created_at)
                },
                target_id: Some(message.id),
                source_key: format!(
                    "message:{}:{}",
                    message.id,
                    if create { "create" } else { "delete" }
                ),
            }
        }
        _ => return Ok(()),
    };

    project_activities::ActiveModel {
        project_id: Set(event.project_id),
        activity_type: Set(event.activity_type.to_owned()),
        action: Set(event.action),
        actor_id: Set(audit.user_id),
        actor_name: Set(actor_name(db, audit.user_id, audit.employee_no.as_deref()).await?),
        occurred_at: Set(event.occurred_at),
        title: Set(event.title),
        summary: Set(event.summary),
        target_id: Set(event.target_id),
        source_key: Set(event.source_key),
        ..Default::default()
    }
    .insert(db)
    .await?;
    Ok(())
}

fn encode_cursor(occurred_at: DateTime<Utc>, id: u64) -> String {
    let micros = occurred_at.timestamp_micros() as u64 ^ (1_u64 << 63);
    format!("{micros:016x}{id:016x}")
}

fn decode_cursor(value: &str) -> ApiResult<(DateTime<Utc>, u64)> {
    if value.len() != 32 || !value.bytes().all(|byte| byte.is_ascii_hexdigit()) {
        return Err(AppError::BadRequest("无效的项目动态游标".into()));
    }
    let micros = u64::from_str_radix(&value[..16], 16)
        .map_err(|_| AppError::BadRequest("无效的项目动态游标".into()))?;
    let id = u64::from_str_radix(&value[16..], 16)
        .map_err(|_| AppError::BadRequest("无效的项目动态游标".into()))?;
    let time = DateTime::<Utc>::from_timestamp_micros((micros ^ (1_u64 << 63)) as i64)
        .ok_or_else(|| AppError::BadRequest("无效的项目动态游标".into()))?;
    if !(1000..=9999).contains(&time.year()) {
        return Err(AppError::BadRequest("无效的项目动态游标".into()));
    }
    Ok((time, id))
}

pub async fn list(
    db: &DatabaseConnection,
    user: &CurrentUser,
    project_id: u64,
    q: &ActivityQuery,
) -> ApiResult<Value> {
    let project = scope::ensure_project_access(db, user, project_id).await?;
    let activity_type = q
        .activity_type
        .as_deref()
        .map(str::trim)
        .filter(|v| !v.is_empty())
        .map(str::to_ascii_uppercase);
    if let Some(value) = activity_type.as_deref() {
        if !matches!(value, "PROJECT" | "FILE" | "MESSAGE") {
            return Err(AppError::BadRequest("非法的项目动态类型".into()));
        }
    }
    let cursor = q.cursor.as_deref().map(decode_cursor).transpose()?;
    let page_size = if q.page_size == 0 {
        DEFAULT_PAGE_SIZE
    } else {
        q.page_size.min(MAX_PAGE_SIZE)
    };
    let mut query = project_activities::Entity::find()
        .filter(project_activities::Column::ProjectId.eq(project_id));
    if let Some(activity_type) = activity_type {
        query = query.filter(project_activities::Column::ActivityType.eq(activity_type));
    }
    if let Some((time, id)) = cursor {
        query = query.filter(
            Condition::any()
                .add(project_activities::Column::OccurredAt.lt(time))
                .add(
                    Condition::all()
                        .add(project_activities::Column::OccurredAt.eq(time))
                        .add(project_activities::Column::Id.lt(id)),
                ),
        );
    }
    let mut rows = query
        .order_by_desc(project_activities::Column::OccurredAt)
        .order_by_desc(project_activities::Column::Id)
        .limit(page_size + 1)
        .all(db)
        .await?;
    let has_more = rows.len() > page_size as usize;
    if has_more {
        rows.pop();
    }

    let file_ids = rows
        .iter()
        .filter(|v| v.activity_type == "FILE")
        .filter_map(|v| v.target_id)
        .collect::<Vec<_>>();
    let message_ids = rows
        .iter()
        .filter(|v| v.activity_type == "MESSAGE")
        .filter_map(|v| v.target_id)
        .collect::<Vec<_>>();
    let available_files = files::Entity::find()
        .filter(files::Column::Id.is_in(file_ids))
        .filter(files::Column::ProjectId.eq(project_id))
        .filter(files::Column::Status.eq(FileStatus::Available))
        .all(db)
        .await?
        .into_iter()
        .map(|v| v.id)
        .collect::<HashSet<_>>();
    let available_messages = messages::Entity::find()
        .filter(messages::Column::Id.is_in(message_ids))
        .filter(messages::Column::ProjectId.eq(project_id))
        .filter(messages::Column::Status.eq(MessageStatus::Normal))
        .all(db)
        .await?
        .into_iter()
        .map(|v| v.id)
        .collect::<HashSet<_>>();
    let list = rows.iter().map(|item| {
        let available = match (item.activity_type.as_str(), item.target_id) {
            ("PROJECT", Some(id)) => id == project_id,
            ("FILE", Some(id)) => available_files.contains(&id),
            ("MESSAGE", Some(id)) => available_messages.contains(&id),
            _ => false,
        };
        json!({
            "id": item.id, "type": item.activity_type, "action": item.action,
            "actorName": item.actor_name, "occurredAt": item.occurred_at,
            "title": item.title,
            "summary": if item.activity_type == "MESSAGE" && !available { None } else { item.summary.as_deref() },
            "targetId": item.target_id, "targetAvailable": available,
        })
    }).collect::<Vec<_>>();
    let next_cursor = if has_more {
        rows.last().map(|v| encode_cursor(v.occurred_at, v.id))
    } else {
        None
    };
    let last_activity_at = project_activities::Entity::find()
        .filter(project_activities::Column::ProjectId.eq(project_id))
        .order_by_desc(project_activities::Column::OccurredAt)
        .order_by_desc(project_activities::Column::Id)
        .one(db)
        .await?
        .map(|v| v.occurred_at);
    Ok(json!({
        "list": list,
        "nextCursor": next_cursor,
        "summary": {
            "status": super::project::status_str(&project.status),
            "pendingConfirmation": project.status == crate::entity::enums::ProjectStatus::PendingConfirmation,
            "confirmSide": project.confirm_side.map(|side| if side == crate::entity::enums::ConfirmSide::Company { "COMPANY" } else { "SUPPLIER" }),
            "lastActivityAt": last_activity_at,
        }
    }))
}
