//! 权限点查询：users → user_roles → role_permissions → permissions（仅启用角色）。
//! 实体未定义 Relation，这里用原生 SQL 一次取回。
use sea_orm::{
    ColumnTrait, ConnectionTrait, DatabaseBackend, EntityTrait, QueryFilter, QuerySelect, Statement,
};

use crate::error::{ApiResult, AppError};

/// Low-frequency account/role/department changes share one stable transaction gate.
/// Call immediately after BEGIN, before any snapshot reads (MySQL REPEATABLE READ).
pub async fn lock_management_state(db: &impl ConnectionTrait) -> ApiResult<()> {
    crate::entity::system_configs::Entity::find()
        .filter(crate::entity::system_configs::Column::CfgKey.eq("security.management_lock"))
        .lock_exclusive()
        .one(db)
        .await?
        .ok_or_else(|| AppError::Internal("权限事务锁配置缺失".into()))?;
    Ok(())
}

/// Business writes share the same stable gate while management changes keep
/// taking it exclusively. MySQL 5.7 requires `LOCK IN SHARE MODE` rather than
/// the `FOR SHARE` emitted by the current SeaORM/sea-query versions.
pub async fn lock_business_state(db: &impl ConnectionTrait) -> ApiResult<()> {
    crate::entity::system_configs::Entity::find()
        .from_raw_sql(Statement::from_sql_and_values(
            DatabaseBackend::MySql,
            "SELECT * FROM system_configs WHERE cfg_key = ? LOCK IN SHARE MODE",
            ["security.management_lock".into()],
        ))
        .one(db)
        .await?
        .ok_or_else(|| AppError::Internal("权限事务锁配置缺失".into()))?;
    Ok(())
}

/// A request waiting for the gate may have lost its account or action permission.
pub async fn recheck_manager(
    db: &impl ConnectionTrait,
    id: u64,
    permission: &str,
) -> ApiResult<()> {
    let user = crate::entity::users::Entity::find_by_id(id)
        .one(db)
        .await?
        .ok_or(AppError::Forbidden)?;
    if user.status != crate::entity::enums::CommonStatus::Active {
        return Err(AppError::Forbidden);
    }
    check_perm(db, id, permission).await
}

pub async fn permission_codes(db: &impl ConnectionTrait, user_id: u64) -> ApiResult<Vec<String>> {
    let rows = db
        .query_all(Statement::from_sql_and_values(
            DatabaseBackend::MySql,
            "SELECT DISTINCT p.code FROM permissions p \
             JOIN role_permissions rp ON rp.permission_id = p.id \
             JOIN user_roles ur ON ur.role_id = rp.role_id \
             JOIN roles r ON r.id = ur.role_id AND r.status = 'ACTIVE' \
             WHERE ur.user_id = ?",
            [user_id.into()],
        ))
        .await?;
    Ok(rows
        .iter()
        .filter_map(|r| r.try_get::<String>("", "code").ok())
        .collect())
}

pub async fn check_perm(db: &impl ConnectionTrait, user_id: u64, code: &str) -> ApiResult<()> {
    if permission_codes(db, user_id)
        .await?
        .iter()
        .any(|c| c == code)
    {
        Ok(())
    } else {
        Err(AppError::Forbidden)
    }
}

/// Called inside the management transaction: delegates cannot grant permissions
/// they do not own, or mutate accounts/roles above their authority ceiling.
pub async fn ensure_grantable_permissions(
    db: &impl ConnectionTrait,
    actor: u64,
    ids: &[u64],
) -> ApiResult<()> {
    if super::scope::is_system_admin(db, actor).await? {
        return Ok(());
    }
    let owned = permission_codes(db, actor).await?;
    let requested = crate::entity::permissions::Entity::find()
        .filter(crate::entity::permissions::Column::Id.is_in(ids.to_vec()))
        .all(db)
        .await?;
    if requested.len() != ids.iter().collect::<std::collections::HashSet<_>>().len()
        || requested.iter().any(|p| !owned.contains(&p.code))
    {
        return Err(AppError::Forbidden);
    }
    Ok(())
}

pub async fn ensure_manage_role(
    db: &impl ConnectionTrait,
    actor: u64,
    role_id: u64,
) -> ApiResult<()> {
    if super::scope::is_system_admin(db, actor).await? {
        return Ok(());
    }
    let role = crate::entity::roles::Entity::find_by_id(role_id)
        .one(db)
        .await?
        .ok_or(AppError::NotFound)?;
    if role.is_built_in && role.name == "系统管理员" {
        return Err(AppError::Forbidden);
    }
    let ids = crate::entity::role_permissions::Entity::find()
        .filter(crate::entity::role_permissions::Column::RoleId.eq(role_id))
        .all(db)
        .await?
        .into_iter()
        .map(|r| r.permission_id)
        .collect::<Vec<_>>();
    ensure_grantable_permissions(db, actor, &ids).await
}

pub async fn ensure_manage_user(
    db: &impl ConnectionTrait,
    actor: u64,
    target: u64,
) -> ApiResult<()> {
    if super::scope::is_system_admin(db, actor).await? {
        return Ok(());
    }
    let bindings = crate::entity::user_roles::Entity::find()
        .filter(crate::entity::user_roles::Column::UserId.eq(target))
        .all(db)
        .await?;
    for binding in bindings {
        ensure_manage_role(db, actor, binding.role_id).await?;
    }
    Ok(())
}
