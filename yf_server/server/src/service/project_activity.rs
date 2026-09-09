//! 项目业务时间线：独立于系统审计日志，按稳定游标分页。
use std::collections::HashSet;

use chrono::{DateTime, Datelike, Utc};
use sea_orm::{
    ActiveModelTrait, ColumnTrait, Condition, ConnectionTrait, DatabaseConnection, EntityTrait,
    PaginatorTrait, QueryFilter, QueryOrder, QuerySelect, Set,
};
use serde::Deserialize;
use serde_json::{json, Value};

use crate::entity::enums::{FileStatus, MessageStatus, ProjectStatus, RoundStatus};
use crate::entity::{
    audit_logs, files, messages, project_activities, projects, round_status_logs, rounds, users,
};
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
    if value.is_empty() {
        None
    } else {
        Some(value.chars().take(160).collect())
    }
}

fn project_status(status: &ProjectStatus) -> &'static str {
    match status {
        ProjectStatus::Draft => "DRAFT",
        ProjectStatus::InProgress => "IN_PROGRESS",
        ProjectStatus::Completed => "COMPLETED",
        ProjectStatus::Terminated => "TERMINATED",
    }
}

fn round_status_for_action(action: &str) -> Option<RoundStatus> {
    match action {
        "ROUND_CREATE" => Some(RoundStatus::Pending),
        "ROUND_CONFIRM" => Some(RoundStatus::Confirmed),
        "ROUND_REJECT" => Some(RoundStatus::Rejected),
        "ROUND_CANCEL" => Some(RoundStatus::Cancelled),
        _ => None,
    }
}

async fn actor_name(
    db: &impl ConnectionTrait,
    user_id: Option<u64>,
    employee_no: Option<&str>,
) -> Result<String, sea_orm::DbErr> {
    if let Some(user_id) = user_id {
        if let Some(user) = users::Entity::find_by_id(user_id).one(db).await? {
            if !user.real_name.trim().is_empty() {
                return Ok(user.real_name);
            }
            if !user.employee_no.trim().is_empty() {
                return Ok(user.employee_no);
            }
        }
    }
    Ok(employee_no
        .filter(|value| !value.trim().is_empty())
        .unwrap_or("未知")
        .to_owned())
}

struct NewActivity {
    project_id: u64,
    activity_type: &'static str,
    action: &'static str,
    title: String,
    summary: Option<String>,
    occurred_at: DateTime<Utc>,
    round_id: Option<u64>,
    round_no: Option<i32>,
    target_id: Option<u64>,
    source_key: String,
}

/// 将业务白名单审计同步镜像到独立时间线。调用者的事务决定两者是否共同提交。
pub async fn capture_audit(db: &impl ConnectionTrait, audit: &audit_logs::Model) -> ApiResult<()> {
    let target_id = match audit.target_id.as_deref() {
        Some(value) => match value.parse::<u64>() {
            Ok(id) if id.to_string() == value => id,
            _ => return Ok(()),
        },
        None => return Ok(()),
    };
    let expected_target_type = if audit.action.starts_with("PROJECT_") {
        "project"
    } else if audit.action.starts_with("ROUND_") {
        "round"
    } else if audit.action.starts_with("FILE_") {
        "file"
    } else if audit.action.starts_with("MESSAGE_") {
        "message"
    } else {
        return Ok(());
    };
    if audit.target_type.as_deref() != Some(expected_target_type) {
        return Ok(());
    }

    let event = match audit.action.as_str() {
        "PROJECT_CREATE" | "PROJECT_UPDATE" | "PROJECT_MEMBERS" | "PROJECT_STATUS"
        | "PROJECT_DELETE" => {
            let Some(project) = projects::Entity::find_by_id(target_id).one(db).await? else {
                return Ok(());
            };
            let (action, title) = match audit.action.as_str() {
                "PROJECT_CREATE" => ("CREATE", "创建项目"),
                "PROJECT_UPDATE" => ("UPDATE", "编辑项目"),
                "PROJECT_MEMBERS" => ("MEMBERS_CHANGE", "调整项目成员"),
                "PROJECT_DELETE" => ("DELETE", "删除项目"),
                "PROJECT_STATUS" => match audit
                    .detail
                    .as_ref()
                    .and_then(|detail| detail.get("to"))
                    .and_then(Value::as_str)
                {
                    Some("IN_PROGRESS") => ("START", "开始项目"),
                    Some("COMPLETED") => ("COMPLETE", "完成项目"),
                    Some("TERMINATED") => ("TERMINATE", "终止项目"),
                    _ => return Ok(()),
                },
                _ => unreachable!(),
            };
            NewActivity {
                project_id: project.id,
                activity_type: "PROJECT",
                action,
                title: title.to_owned(),
                summary: truncate(&project.name),
                occurred_at: if action == "CREATE" {
                    project.created_at
                } else {
                    audit.created_at
                },
                round_id: None,
                round_no: None,
                target_id: Some(project.id),
                source_key: if audit.action == "PROJECT_CREATE" {
                    format!("project:{}:create", project.id)
                } else {
                    format!("audit:{}", audit.id)
                },
            }
        }
        "ROUND_CREATE" | "ROUND_CONFIRM" | "ROUND_REJECT" | "ROUND_CANCEL" => {
            let Some(round) = rounds::Entity::find_by_id(target_id).one(db).await? else {
                return Ok(());
            };
            let expected = round_status_for_action(&audit.action).expect("whitelisted action");
            let Some(log) = round_status_logs::Entity::find()
                .filter(round_status_logs::Column::RoundId.eq(round.id))
                .filter(round_status_logs::Column::ToStatus.eq(expected))
                .order_by_desc(round_status_logs::Column::Id)
                .one(db)
                .await?
            else {
                return Ok(());
            };
            let (action, verb) = match audit.action.as_str() {
                "ROUND_CREATE" => ("CREATE", "创建"),
                "ROUND_CONFIRM" => ("CONFIRM", "确认"),
                "ROUND_REJECT" => ("REJECT", "驳回"),
                "ROUND_CANCEL" => ("CANCEL", "撤销"),
                _ => unreachable!(),
            };
            NewActivity {
                project_id: round.project_id,
                activity_type: "ROUND",
                action,
                title: format!("{verb}第{}轮", round.round_no),
                summary: if action == "REJECT" {
                    log.reason.as_deref().and_then(truncate)
                } else {
                    round
                        .title
                        .as_deref()
                        .or(round.remark.as_deref())
                        .and_then(truncate)
                },
                occurred_at: log.created_at,
                round_id: Some(round.id),
                round_no: Some(round.round_no),
                target_id: Some(round.id),
                source_key: if action == "CREATE" {
                    format!("round:{}:create", round.id)
                } else {
                    format!("round-log:{}", log.id)
                },
            }
        }
        "FILE_UPLOAD" | "FILE_DELETE" => {
            let Some(file) = files::Entity::find_by_id(target_id).one(db).await? else {
                return Ok(());
            };
            let round_no = rounds::Entity::find_by_id(file.round_id)
                .one(db)
                .await?
                .filter(|round| round.project_id == file.project_id)
                .map(|round| round.round_no);
            let (action, title, suffix) = if audit.action == "FILE_UPLOAD" {
                ("UPLOAD", "上传文件", "upload")
            } else {
                ("DELETE", "删除文件", "delete")
            };
            NewActivity {
                project_id: file.project_id,
                activity_type: "FILE",
                action,
                title: title.to_owned(),
                summary: truncate(&file.original_name),
                occurred_at: if action == "UPLOAD" {
                    file.created_at
                } else {
                    file.deleted_at.unwrap_or(audit.created_at)
                },
                round_id: Some(file.round_id),
                round_no,
                target_id: Some(file.id),
                source_key: format!("file:{}:{suffix}", file.id),
            }
        }
        "MESSAGE_CREATE" | "MESSAGE_DELETE" => {
            let Some(message) = messages::Entity::find_by_id(target_id).one(db).await? else {
                return Ok(());
            };
            let round_no = match message.round_id {
                Some(round_id) => rounds::Entity::find_by_id(round_id)
                    .one(db)
                    .await?
                    .filter(|round| round.project_id == message.project_id)
                    .map(|round| round.round_no),
                None => None,
            };
            let is_create = audit.action == "MESSAGE_CREATE";
            NewActivity {
                project_id: message.project_id,
                activity_type: "MESSAGE",
                action: if is_create { "CREATE" } else { "DELETE" },
                title: if is_create {
                    "发表留言"
                } else {
                    "删除留言"
                }
                .to_owned(),
                summary: if is_create {
                    truncate(&message.content)
                } else {
                    None
                },
                occurred_at: if is_create {
                    message.created_at
                } else {
                    message.deleted_at.unwrap_or(audit.created_at)
                },
                round_id: message.round_id,
                round_no,
                target_id: Some(message.id),
                source_key: format!(
                    "message:{}:{}",
                    message.id,
                    if is_create { "create" } else { "delete" }
                ),
            }
        }
        _ => return Ok(()),
    };

    project_activities::ActiveModel {
        project_id: Set(event.project_id),
        activity_type: Set(event.activity_type.to_owned()),
        action: Set(event.action.to_owned()),
        actor_id: Set(audit.user_id),
        actor_name: Set(actor_name(db, audit.user_id, audit.employee_no.as_deref()).await?),
        occurred_at: Set(event.occurred_at),
        title: Set(event.title),
        summary: Set(event.summary),
        round_id: Set(event.round_id),
        round_no: Set(event.round_no),
        target_id: Set(event.target_id),
        source_key: Set(event.source_key),
        ..Default::default()
    }
    .insert(db)
    .await?;
    Ok(())
}

fn encode_cursor(occurred_at: DateTime<Utc>, id: u64) -> String {
    let sortable_micros = occurred_at.timestamp_micros() as u64 ^ (1_u64 << 63);
    format!("{sortable_micros:016x}{id:016x}")
}

fn decode_cursor(value: &str) -> ApiResult<(DateTime<Utc>, u64)> {
    if value.len() != 32 || !value.bytes().all(|byte| byte.is_ascii_hexdigit()) {
        return Err(AppError::BadRequest("无效的项目动态游标".into()));
    }
    let micros = u64::from_str_radix(&value[..16], 16)
        .map_err(|_| AppError::BadRequest("无效的项目动态游标".into()))?;
    let id = u64::from_str_radix(&value[16..], 16)
        .map_err(|_| AppError::BadRequest("无效的项目动态游标".into()))?;
    let signed_micros = (micros ^ (1_u64 << 63)) as i64;
    let occurred_at = DateTime::<Utc>::from_timestamp_micros(signed_micros)
        .ok_or_else(|| AppError::BadRequest("无效的项目动态游标".into()))?;
    if !(1000..=9999).contains(&occurred_at.year()) {
        return Err(AppError::BadRequest("无效的项目动态游标".into()));
    }
    Ok((occurred_at, id))
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
        .filter(|value| !value.is_empty())
        .map(str::to_ascii_uppercase);
    if let Some(value) = activity_type.as_deref() {
        if !matches!(value, "PROJECT" | "ROUND" | "FILE" | "MESSAGE") {
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
    if let Some((occurred_at, id)) = cursor {
        query = query.filter(
            Condition::any()
                .add(project_activities::Column::OccurredAt.lt(occurred_at))
                .add(
                    Condition::all()
                        .add(project_activities::Column::OccurredAt.eq(occurred_at))
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

    let round_ids: Vec<u64> = rows
        .iter()
        .filter(|item| item.activity_type == "ROUND")
        .filter_map(|item| item.target_id)
        .collect();
    let file_ids: Vec<u64> = rows
        .iter()
        .filter(|item| item.activity_type == "FILE")
        .filter_map(|item| item.target_id)
        .collect();
    let message_ids: Vec<u64> = rows
        .iter()
        .filter(|item| item.activity_type == "MESSAGE")
        .filter_map(|item| item.target_id)
        .collect();
    let available_rounds: HashSet<u64> = rounds::Entity::find()
        .filter(rounds::Column::Id.is_in(round_ids))
        .filter(rounds::Column::ProjectId.eq(project_id))
        .all(db)
        .await?
        .into_iter()
        .map(|item| item.id)
        .collect();
    let available_files: HashSet<u64> = files::Entity::find()
        .filter(files::Column::Id.is_in(file_ids))
        .filter(files::Column::ProjectId.eq(project_id))
        .filter(files::Column::Status.eq(FileStatus::Available))
        .all(db)
        .await?
        .into_iter()
        .map(|item| item.id)
        .collect();
    let available_messages: HashSet<u64> = messages::Entity::find()
        .filter(messages::Column::Id.is_in(message_ids))
        .filter(messages::Column::ProjectId.eq(project_id))
        .filter(messages::Column::Status.eq(MessageStatus::Normal))
        .all(db)
        .await?
        .into_iter()
        .map(|item| item.id)
        .collect();

    let list: Vec<Value> = rows
        .iter()
        .map(|item| {
            let target_available = match (item.activity_type.as_str(), item.target_id) {
                ("PROJECT", Some(id)) => id == project_id,
                ("ROUND", Some(id)) => available_rounds.contains(&id),
                ("FILE", Some(id)) => available_files.contains(&id),
                ("MESSAGE", Some(id)) => available_messages.contains(&id),
                _ => false,
            };
            let summary = if item.activity_type == "MESSAGE" && !target_available {
                None
            } else {
                item.summary.as_deref()
            };
            json!({
                "id": item.id,
                "type": item.activity_type,
                "action": item.action,
                "actorName": item.actor_name,
                "occurredAt": item.occurred_at,
                "title": item.title,
                "summary": summary,
                "roundId": item.round_id,
                "roundNo": item.round_no,
                "targetId": item.target_id,
                "targetAvailable": target_available,
            })
        })
        .collect();
    let next_cursor = if has_more {
        rows.last()
            .map(|item| encode_cursor(item.occurred_at, item.id))
    } else {
        None
    };
    let pending_rounds = rounds::Entity::find()
        .filter(rounds::Column::ProjectId.eq(project_id))
        .filter(rounds::Column::Status.eq(RoundStatus::Pending))
        .count(db)
        .await?;
    let last_activity_at = project_activities::Entity::find()
        .filter(project_activities::Column::ProjectId.eq(project_id))
        .order_by_desc(project_activities::Column::OccurredAt)
        .order_by_desc(project_activities::Column::Id)
        .one(db)
        .await?
        .map(|item| item.occurred_at);

    Ok(json!({
        "list": list,
        "nextCursor": next_cursor,
        "summary": {
            "status": project_status(&project.status),
            "pendingRounds": pending_rounds,
            "lastActivityAt": last_activity_at,
        }
    }))
}
