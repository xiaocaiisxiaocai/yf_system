//! 数据范围：项目可见性判断与查询条件注入（《02-数据库设计》§4.1）。
use sea_orm::{ColumnTrait, Condition, ConnectionTrait, EntityTrait, QueryFilter};

use crate::entity::enums::{CommonStatus, UserType};
use crate::entity::{project_members, projects, roles, user_roles};
use crate::error::{ApiResult, AppError};
use crate::middleware::auth::CurrentUser;

/// 是否拥有 project:view_all 权限点（管理层/管理员）
pub async fn can_view_all(db: &impl ConnectionTrait, user_id: u64) -> ApiResult<bool> {
    Ok(super::perm::permission_codes(db, user_id)
        .await?
        .iter()
        .any(|c| c == "project:view_all"))
}

/// 系统管理员身份不可由普通可配置权限点替代，用于文件删除等不可恢复的高风险操作。
pub async fn is_system_admin(db: &impl ConnectionTrait, user_id: u64) -> ApiResult<bool> {
    let admin_role = roles::Entity::find()
        .filter(roles::Column::IsBuiltIn.eq(true))
        .filter(roles::Column::Name.eq("系统管理员"))
        .filter(roles::Column::Status.eq(CommonStatus::Active))
        .one(db)
        .await?;
    let Some(role) = admin_role else {
        return Ok(false);
    };
    Ok(user_roles::Entity::find_by_id((user_id, role.id))
        .one(db)
        .await?
        .is_some())
}

/// 当前用户可见的项目 id 集合；None 表示"全部可见"（view_all）
pub async fn visible_project_ids(
    db: &impl ConnectionTrait,
    user: &CurrentUser,
) -> ApiResult<Option<Vec<u64>>> {
    if user.user_type == UserType::Supplier {
        // 供应商：本供应商名下全部项目
        let sid = user.supplier_id.ok_or(AppError::OutOfScope)?;
        let ids = projects::Entity::find()
            .filter(projects::Column::SupplierId.eq(sid))
            .all(db)
            .await?
            .into_iter()
            .map(|p| p.id)
            .collect();
        return Ok(Some(ids));
    }
    if can_view_all(db, user.id).await? {
        return Ok(None);
    }
    let member_ids: Vec<u64> = project_members::Entity::find()
        .filter(project_members::Column::UserId.eq(user.id))
        .all(db)
        .await?
        .into_iter()
        .map(|m| m.project_id)
        .collect();
    let created_ids: Vec<u64> = projects::Entity::find()
        .filter(projects::Column::CreatedBy.eq(user.id))
        .all(db)
        .await?
        .into_iter()
        .map(|p| p.id)
        .collect();
    let mut ids = member_ids;
    ids.extend(created_ids);
    ids.sort_unstable();
    ids.dedup();
    Ok(Some(ids))
}

/// 项目列表查询的可见性条件（叠加在分页查询上）
pub async fn project_condition(
    db: &impl ConnectionTrait,
    user: &CurrentUser,
) -> ApiResult<Condition> {
    Ok(match visible_project_ids(db, user).await? {
        None => Condition::all(),
        Some(ids) => Condition::all().add(projects::Column::Id.is_in(ids)),
    })
}

/// 校验并返回项目（不可见即 40302，不存在即 40401）
pub async fn ensure_project_access(
    db: &impl ConnectionTrait,
    user: &CurrentUser,
    project_id: u64,
) -> ApiResult<projects::Model> {
    let project = projects::Entity::find_by_id(project_id)
        .one(db)
        .await?
        .ok_or(AppError::NotFound)?;
    let ok = if user.user_type == UserType::Supplier {
        Some(project.supplier_id) == user.supplier_id
    } else {
        can_view_all(db, user.id).await?
            || project.created_by == user.id
            || project_members::Entity::find_by_id((project_id, user.id))
                .one(db)
                .await?
                .is_some()
    };
    if ok {
        Ok(project)
    } else {
        Err(AppError::OutOfScope)
    }
}
