//! 数据范围：项目可见性判断与查询条件注入（《02-数据库设计》§4.1）。
use sea_orm::{
    ColumnTrait, Condition, ConnectionTrait, DatabaseBackend, EntityTrait, QueryFilter, Statement,
};

use crate::entity::enums::{CommonStatus, UserType};
use crate::entity::{project_members, projects, roles, suppliers, user_roles, users};
use crate::error::{ApiResult, AppError};
use crate::middleware::auth::CurrentUser;

async fn lock_user_shared(db: &impl ConnectionTrait, user_id: u64) -> ApiResult<users::Model> {
    // SeaORM 1 / sea-query 0.32 emits `FOR SHARE`, which MySQL 5.7 does not support.
    users::Entity::find()
        .from_raw_sql(Statement::from_sql_and_values(
            DatabaseBackend::MySql,
            "SELECT * FROM users WHERE id = ? LOCK IN SHARE MODE",
            [user_id.into()],
        ))
        .one(db)
        .await?
        .ok_or(AppError::Forbidden)
}

async fn lock_supplier_shared(
    db: &impl ConnectionTrait,
    supplier_id: u64,
) -> ApiResult<suppliers::Model> {
    suppliers::Entity::find()
        .from_raw_sql(Statement::from_sql_and_values(
            DatabaseBackend::MySql,
            "SELECT * FROM suppliers WHERE id = ? LOCK IN SHARE MODE",
            [supplier_id.into()],
        ))
        .one(db)
        .await?
        .ok_or(AppError::Forbidden)
}

async fn ensure_supplier_active(db: &impl ConnectionTrait, supplier_id: u64) -> ApiResult<()> {
    if lock_supplier_shared(db, supplier_id).await?.status != CommonStatus::Active {
        return Err(AppError::Forbidden);
    }
    Ok(())
}

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

/// 不可恢复操作必须由当前仍有效的内部系统管理员执行。
pub async fn require_system_admin(db: &impl ConnectionTrait, user_id: u64) -> ApiResult<()> {
    let user = lock_user_shared(db, user_id).await?;
    if user.status != CommonStatus::Active
        || user.user_type != UserType::Internal
        || !is_system_admin(db, user_id).await?
    {
        return Err(AppError::Forbidden);
    }
    Ok(())
}

/// 当前用户可见的项目 id 集合；None 表示"全部可见"（view_all）
pub async fn visible_project_ids(
    db: &impl ConnectionTrait,
    user: &CurrentUser,
) -> ApiResult<Option<Vec<u64>>> {
    if user.user_type == UserType::Supplier {
        // 供应商：本供应商名下全部项目
        let sid = user.supplier_id.ok_or(AppError::OutOfScope)?;
        ensure_supplier_active(db, sid).await?;
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
    // 事务内调用会持有共享锁到提交，与账号物理删除的目标行排他锁配对，
    // 防止等待期间继续用已删除或已变更归属的 CurrentUser 写业务历史。
    let current = lock_user_shared(db, user.id).await?;
    if current.status != CommonStatus::Active
        || current.user_type != user.user_type
        || current.supplier_id != user.supplier_id
    {
        return Err(AppError::Forbidden);
    }
    let active_supplier_id = if current.user_type == UserType::Supplier {
        let supplier_id = current.supplier_id.ok_or(AppError::Forbidden)?;
        ensure_supplier_active(db, supplier_id).await?;
        Some(supplier_id)
    } else {
        None
    };
    let project = projects::Entity::find_by_id(project_id)
        .one(db)
        .await?
        .ok_or(AppError::NotFound)?;
    let ok = if let Some(supplier_id) = active_supplier_id {
        project.supplier_id == supplier_id
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
