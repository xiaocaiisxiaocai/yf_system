//! 工作台汇总：可见项目数、待我方确认项目、未读留言
use sea_orm::{
    ColumnTrait, Condition, DatabaseConnection, EntityTrait, PaginatorTrait, QueryFilter,
    QueryOrder, QuerySelect,
};
use serde_json::{json, Value};

use crate::entity::enums::{ConfirmSide, MessageStatus, ProjectStatus, UserType};
use crate::entity::{message_reads, messages, projects};
use crate::error::ApiResult;
use crate::middleware::auth::CurrentUser;

use super::scope;

pub async fn pending_projects(
    db: &DatabaseConnection,
    user: &CurrentUser,
    q: &crate::dto::PageQuery,
) -> ApiResult<crate::dto::PageResp<Value>> {
    let (page, size) = q.clamped();
    if !can_confirm(db, user).await? {
        return Ok(crate::dto::PageResp::new(vec![], 0, page, size));
    }
    let cond = scope::project_condition(db, user).await?;
    let paginator = projects::Entity::find()
        .filter(cond)
        .filter(projects::Column::Status.eq(ProjectStatus::PendingConfirmation))
        .filter(projects::Column::ConfirmSide.eq(confirmation_side(user)))
        .order_by_desc(projects::Column::UpdatedAt)
        .order_by_desc(projects::Column::Id)
        .paginate(db, size);
    let total = paginator.num_items().await?;
    let rows = paginator.fetch_page(page - 1).await?;
    let list = rows.iter().map(|project| json!({
        "id": project.id,
        "name": project.name,
        "status": "PENDING_CONFIRMATION",
        "confirmSide": if project.confirm_side == Some(ConfirmSide::Company) { "COMPANY" } else { "SUPPLIER" },
        "updatedAt": project.updated_at,
    })).collect();
    Ok(crate::dto::PageResp::new(list, total, page, size))
}

fn confirmation_side(user: &CurrentUser) -> ConfirmSide {
    if user.user_type == UserType::Supplier {
        ConfirmSide::Supplier
    } else {
        ConfirmSide::Company
    }
}

async fn can_confirm(db: &DatabaseConnection, user: &CurrentUser) -> ApiResult<bool> {
    Ok(super::perm::permission_codes(db, user.id)
        .await?
        .iter()
        .any(|code| code == "project:confirm"))
}

pub async fn summary(db: &DatabaseConnection, user: &CurrentUser) -> ApiResult<Value> {
    let cond = scope::project_condition(db, user).await?;
    let visible = projects::Entity::find().filter(cond).all(db).await?;
    let project_ids: Vec<u64> = visible.iter().map(|p| p.id).collect();
    let active_count = visible
        .iter()
        .filter(|p| p.status == ProjectStatus::InProgress)
        .count();

    let my_side = confirmation_side(user);
    let pending_confirmations = if can_confirm(db, user).await? {
        visible
            .iter()
            .filter(|project| {
                project.status == ProjectStatus::PendingConfirmation
                    && project.confirm_side == Some(my_side)
            })
            .count()
    } else {
        0
    };

    // 未读数：2 次查询代替逐项目循环——先取可见项目的他人留言 id，再减去我已读的
    let unread: u64 = if project_ids.is_empty() {
        0
    } else {
        let msg_ids: Vec<u64> = messages::Entity::find()
            .filter(messages::Column::ProjectId.is_in(project_ids.clone()))
            .filter(messages::Column::Status.eq(MessageStatus::Normal))
            .filter(messages::Column::SenderId.ne(user.id))
            .all(db)
            .await?
            .into_iter()
            .map(|m| m.id)
            .collect();
        if msg_ids.is_empty() {
            0
        } else {
            let read_ids: std::collections::HashSet<u64> = message_reads::Entity::find()
                .filter(message_reads::Column::UserId.eq(user.id))
                .filter(message_reads::Column::MessageId.is_in(msg_ids.to_vec()))
                .all(db)
                .await?
                .into_iter()
                .map(|r| r.message_id)
                .collect();
            msg_ids.iter().filter(|id| !read_ids.contains(id)).count() as u64
        }
    };

    // 最近 5 条对我可见的留言（数据库侧分页，发送人批量取）
    let recent_messages = if project_ids.is_empty() {
        vec![]
    } else {
        let msgs = messages::Entity::find()
            .filter(
                Condition::all()
                    .add(messages::Column::ProjectId.is_in(project_ids.clone()))
                    .add(messages::Column::Status.eq(MessageStatus::Normal)),
            )
            .order_by_desc(messages::Column::Id)
            .limit(5)
            .all(db)
            .await?;
        let sender_map: std::collections::HashMap<u64, String> =
            crate::entity::users::Entity::find()
                .filter(
                    crate::entity::users::Column::Id
                        .is_in(msgs.iter().map(|m| m.sender_id).collect::<Vec<_>>()),
                )
                .all(db)
                .await?
                .into_iter()
                .map(|u| (u.id, u.real_name))
                .collect();
        let recent_read_ids: std::collections::HashSet<u64> = if msgs.is_empty() {
            std::collections::HashSet::new()
        } else {
            message_reads::Entity::find()
                .filter(message_reads::Column::UserId.eq(user.id))
                .filter(
                    message_reads::Column::MessageId
                        .is_in(msgs.iter().map(|m| m.id).collect::<Vec<_>>()),
                )
                .all(db)
                .await?
                .into_iter()
                .map(|r| r.message_id)
                .collect()
        };
        msgs.iter()
            .map(|m| {
                let project = visible.iter().find(|p| p.id == m.project_id);
                json!({
                    "id": m.id, "projectId": m.project_id,
                    "projectName": project.map(|p| p.name.clone()),
                    "content": m.content.chars().take(60).collect::<String>(),
                    "senderName": sender_map.get(&m.sender_id),
                    "createdAt": m.created_at,
                    "unread": m.sender_id != user.id && !recent_read_ids.contains(&m.id),
                })
            })
            .collect()
    };

    Ok(json!({
        "projectCount": project_ids.len(),
        "activeProjectCount": active_count,
        "pendingConfirmations": pending_confirmations,
        "unreadMessages": unread,
        "recentMessages": recent_messages,
    }))
}
