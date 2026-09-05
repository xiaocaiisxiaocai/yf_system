//! 留言：时间线 + 按人已读（已读 x/y）+ 邮件通知入队
use chrono::Utc;
use sea_orm::sea_query::OnConflict;
use sea_orm::{
    ActiveModelTrait, ColumnTrait, Condition, ConnectionTrait, DatabaseConnection, EntityTrait,
    PaginatorTrait, QueryFilter, QueryOrder, QuerySelect, Set, TransactionTrait,
};
use serde::Deserialize;
use serde_json::{json, Value};

use crate::dto::PageResp;
use crate::entity::enums::{CommonStatus, MessageStatus, ProjectStatus, RoundStatus};
use crate::entity::{message_reads, messages, project_members, projects, rounds, users};
use crate::error::{ApiResult, AppError};
use crate::middleware::auth::CurrentUser;

use super::{audit, notify, scope};

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct MessageListQuery {
    #[serde(default = "crate::dto::default_page")]
    pub page: u64,
    #[serde(default = "crate::dto::default_page_size")]
    pub page_size: u64,
    /// 不传=全部；传 0=仅项目级；传 id=该轮次
    pub round_id: Option<u64>,
    /// Stable continuation when earlier messages are inserted or deleted.
    pub before_id: Option<u64>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct MessageCreate {
    pub content: String,
    pub round_id: Option<u64>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct MarkRead {
    pub ids: Vec<u64>,
}

/// 项目可见用户数（不含发送者）：内部成员 + 该供应商启用账号
async fn visible_user_ids(
    db: &DatabaseConnection,
    project_id: u64,
    supplier_id: u64,
) -> ApiResult<Vec<u64>> {
    let mut ids: Vec<u64> = project_members::Entity::find()
        .filter(project_members::Column::ProjectId.eq(project_id))
        .all(db)
        .await?
        .into_iter()
        .map(|m| m.user_id)
        .collect();
    let supplier_users: Vec<u64> = users::Entity::find()
        .filter(users::Column::SupplierId.eq(supplier_id))
        .filter(users::Column::Status.eq(CommonStatus::Active))
        .all(db)
        .await?
        .into_iter()
        .map(|u| u.id)
        .collect();
    ids.extend(supplier_users);
    ids.sort_unstable();
    ids.dedup();
    Ok(ids)
}

fn message_value(
    m: &messages::Model,
    sender: &users::Model,
    reads: &[message_reads::Model],
    visible: &[u64],
    viewer_id: u64,
) -> Value {
    let read_by_me = reads.iter().any(|r| r.user_id == viewer_id);
    let total_readers = visible.iter().filter(|&&id| id != m.sender_id).count();
    // 只统计项目参与者内的已读；有 view_all 权限的非成员查看不影响回执
    let read_count = reads
        .iter()
        .filter(|r| r.user_id != m.sender_id && visible.contains(&r.user_id))
        .count();
    json!({
        "id": m.id, "projectId": m.project_id, "roundId": m.round_id,
        "content": m.content,
        "status": if m.status == MessageStatus::Normal { "NORMAL" } else { "DELETED" },
        "senderId": m.sender_id, "senderName": sender.real_name,
        "senderType": sender.user_type.as_str(),
        "createdAt": m.created_at,
        "readCount": read_count,
        "totalCount": total_readers,
        "readByMe": read_by_me,
    })
}

async fn message_json(
    db: &DatabaseConnection,
    m: &messages::Model,
    visible: &[u64],
    viewer_id: u64,
) -> ApiResult<Value> {
    let sender = users::Entity::find_by_id(m.sender_id)
        .one(db)
        .await?
        .ok_or(AppError::NotFound)?;
    let reads = message_reads::Entity::find()
        .filter(message_reads::Column::MessageId.eq(m.id))
        .all(db)
        .await?;
    Ok(message_value(m, &sender, &reads, visible, viewer_id))
}

pub async fn list(
    db: &DatabaseConnection,
    user: &CurrentUser,
    project_id: u64,
    q: &MessageListQuery,
) -> ApiResult<PageResp<Value>> {
    let project = scope::ensure_project_access(db, user, project_id).await?;
    let (page, size) = crate::dto::clamp_page(q.page, q.page_size);
    let mut cond = Condition::all()
        .add(messages::Column::ProjectId.eq(project_id))
        .add(messages::Column::Status.eq(MessageStatus::Normal));
    if let Some(rid) = q.round_id {
        cond = if rid == 0 {
            cond.add(messages::Column::RoundId.is_null())
        } else {
            cond.add(messages::Column::RoundId.eq(rid))
        };
    }
    let total = messages::Entity::find()
        .filter(cond.clone())
        .count(db)
        .await?;
    if let Some(id) = q.before_id {
        cond = cond.add(messages::Column::Id.lt(id));
    }
    let paginator = messages::Entity::find()
        .filter(cond)
        .order_by_desc(messages::Column::Id)
        .paginate(db, size);
    let items = paginator
        .fetch_page(if q.before_id.is_some() { 0 } else { page - 1 })
        .await?;

    let visible = visible_user_ids(db, project_id, project.supplier_id).await?;
    let sender_map: std::collections::HashMap<u64, users::Model> = users::Entity::find()
        .filter(users::Column::Id.is_in(items.iter().map(|m| m.sender_id).collect::<Vec<_>>()))
        .all(db)
        .await?
        .into_iter()
        .map(|u| (u.id, u))
        .collect();
    let reads = message_reads::Entity::find()
        .filter(
            message_reads::Column::MessageId.is_in(items.iter().map(|m| m.id).collect::<Vec<_>>()),
        )
        .all(db)
        .await?;
    let mut reads_by_message: std::collections::HashMap<u64, Vec<message_reads::Model>> =
        Default::default();
    for read in reads {
        reads_by_message
            .entry(read.message_id)
            .or_default()
            .push(read);
    }
    let mut list = Vec::with_capacity(items.len());
    for m in &items {
        let sender = sender_map.get(&m.sender_id).ok_or(AppError::NotFound)?;
        let message_reads = reads_by_message
            .get(&m.id)
            .map(Vec::as_slice)
            .unwrap_or_default();
        list.push(message_value(m, sender, message_reads, &visible, user.id));
    }
    Ok(PageResp::new(list, total, page, size))
}

/// 留言可写规则：项目级要求项目未完结；轮级要求轮次 PENDING
async fn ensure_writable(
    db: &impl ConnectionTrait,
    project: &crate::entity::projects::Model,
    round_id: Option<u64>,
) -> ApiResult<()> {
    if matches!(
        project.status,
        ProjectStatus::Completed | ProjectStatus::Terminated
    ) {
        return Err(AppError::Conflict("项目已完结，不可留言".into()));
    }
    if let Some(rid) = round_id {
        let r = rounds::Entity::find_by_id(rid)
            .lock_exclusive()
            .one(db)
            .await?
            .ok_or(AppError::BadRequest("轮次不存在".into()))?;
        if r.project_id != project.id {
            return Err(AppError::BadRequest("轮次不属于该项目".into()));
        }
        if r.status != RoundStatus::Pending {
            return Err(AppError::Conflict(
                "该轮次已关闭，请在项目级留言或另开新轮".into(),
            ));
        }
    }
    Ok(())
}

pub async fn create(
    db: &DatabaseConnection,
    user: &CurrentUser,
    project_id: u64,
    req: &MessageCreate,
    base_url: &str,
) -> ApiResult<Value> {
    let content = req.content.trim();
    // 与前端 maxLength=4000 对齐：按字符数而非字节数
    if content.is_empty() || content.chars().count() > 4000 {
        return Err(AppError::BadRequest(
            "留言内容为空或超长（≤4000 字）".into(),
        ));
    }
    // 留言与通知入队同事务
    let txn = db.begin().await?;
    // 与轮次、成员调整和文件提交使用同一锁顺序，锁内复查最新状态与授权。
    let project = projects::Entity::find_by_id(project_id)
        .lock_exclusive()
        .one(&txn)
        .await?
        .ok_or(AppError::NotFound)?;
    scope::ensure_project_access(&txn, user, project_id).await?;
    ensure_writable(&txn, &project, req.round_id).await?;
    let model = messages::ActiveModel {
        project_id: Set(project_id),
        round_id: Set(req.round_id),
        sender_id: Set(user.id),
        content: Set(content.to_string()),
        status: Set(MessageStatus::Normal),
        deleted_by: Set(None),
        deleted_at: Set(None),
        created_at: Set(Utc::now()),
        ..Default::default()
    }
    .insert(&txn)
    .await?;

    notify::enqueue_message_notice(&txn, &project, &model, user, base_url).await?;
    audit::insert(
        &txn,
        Some(user.id),
        Some(user.username.clone()),
        "MESSAGE_CREATE",
        Some("message"),
        Some(model.id.to_string()),
        Some(json!({"projectId": project_id, "roundId": req.round_id})),
        None,
    )
    .await?;
    txn.commit().await?;

    let visible = visible_user_ids(db, project_id, project.supplier_id).await?;
    message_json(db, &model, &visible, user.id).await
}

/// 批量已读（幂等）
pub async fn mark_read(
    db: &DatabaseConnection,
    user: &CurrentUser,
    req: &MarkRead,
) -> ApiResult<()> {
    if req.ids.len() > 500 {
        return Err(AppError::BadRequest("单次标记数量超过上限".into()));
    }
    // 批量取消息，按项目分组做权限校验，避免逐条 2 次查询
    let msgs = messages::Entity::find()
        .filter(messages::Column::Id.is_in(req.ids.clone()))
        .filter(messages::Column::SenderId.ne(user.id))
        .all(db)
        .await?;
    let mut checked_projects: std::collections::HashSet<u64> = std::collections::HashSet::new();
    let txn = db.begin().await?;
    for m in &msgs {
        if !checked_projects.contains(&m.project_id) {
            if scope::ensure_project_access(db, user, m.project_id)
                .await
                .is_err()
            {
                continue;
            }
            checked_projects.insert(m.project_id);
        }
        message_reads::Entity::insert(message_reads::ActiveModel {
            message_id: Set(m.id),
            user_id: Set(user.id),
            read_at: Set(Utc::now()),
        })
        .on_conflict(
            OnConflict::columns([
                message_reads::Column::MessageId,
                message_reads::Column::UserId,
            ])
            .update_column(message_reads::Column::ReadAt)
            .to_owned(),
        )
        .exec(&txn)
        .await?;
    }
    txn.commit().await?;
    Ok(())
}

pub async fn reads(
    db: &DatabaseConnection,
    user: &CurrentUser,
    message_id: u64,
) -> ApiResult<Value> {
    let m = messages::Entity::find_by_id(message_id)
        .filter(messages::Column::Status.eq(MessageStatus::Normal))
        .one(db)
        .await?
        .ok_or(AppError::NotFound)?;
    let project = scope::ensure_project_access(db, user, m.project_id).await?;
    let visible = visible_user_ids(db, m.project_id, project.supplier_id).await?;
    let reads = message_reads::Entity::find()
        .filter(message_reads::Column::MessageId.eq(message_id))
        .all(db)
        .await?;
    let read_map: std::collections::HashMap<u64, _> =
        reads.iter().map(|r| (r.user_id, r.read_at)).collect();
    let user_map: std::collections::HashMap<u64, users::Model> = users::Entity::find()
        .filter(users::Column::Id.is_in(visible.clone()))
        .all(db)
        .await?
        .into_iter()
        .map(|u| (u.id, u))
        .collect();
    let mut readers = Vec::new();
    let mut unread = Vec::new();
    for uid in visible.iter().filter(|&&id| id != m.sender_id) {
        if let Some(u) = user_map.get(uid) {
            let item = json!({
                "userId": u.id, "realName": u.real_name,
                "userType": u.user_type.as_str(),
                "readAt": read_map.get(&u.id),
            });
            if read_map.contains_key(&u.id) {
                readers.push(item);
            } else {
                unread.push(item);
            }
        }
    }
    Ok(json!({ "readers": readers, "unread": unread }))
}

pub async fn delete(db: &DatabaseConnection, user: &CurrentUser, message_id: u64) -> ApiResult<()> {
    let m = messages::Entity::find_by_id(message_id)
        .one(db)
        .await?
        .ok_or(AppError::NotFound)?;
    scope::ensure_project_access(db, user, m.project_id).await?;
    if m.status != MessageStatus::Normal {
        return Err(AppError::NotFound);
    }
    let txn = db.begin().await?;
    let mut am: messages::ActiveModel = m.into();
    am.status = Set(MessageStatus::Deleted);
    am.deleted_by = Set(Some(user.id));
    am.deleted_at = Set(Some(Utc::now()));
    am.update(&txn).await?;
    audit::insert(
        &txn,
        Some(user.id),
        Some(user.username.clone()),
        "MESSAGE_DELETE",
        Some("message"),
        Some(message_id.to_string()),
        None,
        None,
    )
    .await?;
    txn.commit().await?;
    Ok(())
}

/// 当前用户在某项目的未读留言数
pub async fn unread_count(
    db: &DatabaseConnection,
    user_id: u64,
    project_id: u64,
) -> ApiResult<u64> {
    let msgs = messages::Entity::find()
        .filter(messages::Column::ProjectId.eq(project_id))
        .filter(messages::Column::Status.eq(MessageStatus::Normal))
        .filter(messages::Column::SenderId.ne(user_id))
        .all(db)
        .await?;
    if msgs.is_empty() {
        return Ok(0);
    }
    let reads = message_reads::Entity::find()
        .filter(message_reads::Column::UserId.eq(user_id))
        .all(db)
        .await?;
    let read_ids: std::collections::HashSet<u64> =
        reads.into_iter().map(|r| r.message_id).collect();
    Ok(msgs.iter().filter(|m| !read_ids.contains(&m.id)).count() as u64)
}
