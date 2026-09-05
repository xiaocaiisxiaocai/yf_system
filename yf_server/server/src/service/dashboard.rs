//! 工作台汇总：可见项目数、待我方确认轮次、未读留言
use sea_orm::{
    ColumnTrait, Condition, DatabaseConnection, EntityTrait, PaginatorTrait, QueryFilter,
    QueryOrder, QuerySelect,
};
use serde_json::{json, Value};

use crate::entity::enums::{ConfirmSide, MessageStatus, ProjectStatus, RoundStatus, UserType};
use crate::entity::{message_reads, messages, projects, rounds};
use crate::error::ApiResult;
use crate::middleware::auth::CurrentUser;

use super::scope;

pub async fn summary(db: &DatabaseConnection, user: &CurrentUser) -> ApiResult<Value> {
    let cond = scope::project_condition(db, user).await?;
    let visible = projects::Entity::find().filter(cond).all(db).await?;
    let project_ids: Vec<u64> = visible.iter().map(|p| p.id).collect();
    let active_count = visible
        .iter()
        .filter(|p| p.status == ProjectStatus::InProgress)
        .count();

    let my_side = if user.user_type == UserType::Supplier {
        ConfirmSide::Supplier
    } else {
        ConfirmSide::Company
    };
    let pending_rounds = if project_ids.is_empty() {
        0
    } else {
        rounds::Entity::find()
            .filter(rounds::Column::ProjectId.is_in(project_ids.clone()))
            .filter(rounds::Column::Status.eq(RoundStatus::Pending))
            .filter(rounds::Column::ConfirmSide.eq(my_side))
            .count(db)
            .await?
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
        msgs.iter()
            .map(|m| {
                let project = visible.iter().find(|p| p.id == m.project_id);
                json!({
                    "id": m.id, "projectId": m.project_id,
                    "projectName": project.map(|p| p.name.clone()),
                    "content": m.content.chars().take(60).collect::<String>(),
                    "senderName": sender_map.get(&m.sender_id),
                    "createdAt": m.created_at,
                })
            })
            .collect()
    };

    Ok(json!({
        "projectCount": project_ids.len(),
        "activeProjectCount": active_count,
        "pendingRounds": pending_rounds,
        "unreadMessages": unread,
        "recentMessages": recent_messages,
    }))
}
