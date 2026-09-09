//! Active project participants shared by notification recipients and message receipts.
use sea_orm::{ColumnTrait, Condition, ConnectionTrait, EntityTrait, QueryFilter, QueryOrder};

use crate::entity::enums::{CommonStatus, UserType};
use crate::entity::{project_members, projects, suppliers, users};
use crate::error::ApiResult;

pub async fn accounts(
    db: &impl ConnectionTrait,
    project: &projects::Model,
) -> ApiResult<Vec<users::Model>> {
    let mut internal_ids: Vec<u64> = project_members::Entity::find()
        .filter(project_members::Column::ProjectId.eq(project.id))
        .all(db)
        .await?
        .into_iter()
        .map(|m| m.user_id)
        .collect();
    internal_ids.push(project.created_by);
    let mut eligible = Condition::any().add(
        Condition::all()
            .add(users::Column::UserType.eq(UserType::Internal))
            .add(users::Column::Id.is_in(internal_ids)),
    );
    if suppliers::Entity::find_by_id(project.supplier_id)
        .filter(suppliers::Column::Status.eq(CommonStatus::Active))
        .one(db)
        .await?
        .is_some()
    {
        eligible = eligible.add(
            Condition::all()
                .add(users::Column::UserType.eq(UserType::Supplier))
                .add(users::Column::SupplierId.eq(project.supplier_id)),
        );
    }
    Ok(users::Entity::find()
        .filter(users::Column::Status.eq(CommonStatus::Active))
        .filter(eligible)
        .order_by_asc(users::Column::Id)
        .all(db)
        .await?)
}
