//! 权限点查询：users → user_roles → role_permissions → permissions（仅启用角色）。
//! 实体未定义 Relation，这里用原生 SQL 一次取回。
use sea_orm::{
    ColumnTrait, ConnectionTrait, DatabaseBackend, EntityTrait, QueryFilter, QuerySelect, Statement,
};

use crate::error::{ApiResult, AppError};

/// Low-frequency account/role/department changes share one stable transaction gate.
/// Call immediately after BEGIN, before any snapshot reads (MySQL REPEATABLE READ).
pub async fn lock_management_state(db: &impl ConnectionTrait) -> ApiResult<()> {
    crate::entity::roles::Entity::find()
        .filter(crate::entity::roles::Column::IsBuiltIn.eq(true))
        .filter(crate::entity::roles::Column::Name.eq("系统管理员"))
        .lock_exclusive()
        .one(db)
        .await?
        .ok_or_else(|| AppError::Internal("系统管理员角色缺失".into()))?;
    Ok(())
}

/// Business writes share the same stable gate while management changes keep
/// taking it exclusively. MySQL 5.7 requires `LOCK IN SHARE MODE` rather than
/// the `FOR SHARE` emitted by the current SeaORM/sea-query versions.
pub async fn lock_business_state(db: &impl ConnectionTrait) -> ApiResult<()> {
    crate::entity::roles::Entity::find()
        .from_raw_sql(Statement::from_sql_and_values(
            DatabaseBackend::MySql,
            "SELECT * FROM roles WHERE is_built_in = TRUE AND name = ? LOCK IN SHARE MODE",
            ["系统管理员".into()],
        ))
        .one(db)
        .await?
        .ok_or_else(|| AppError::Internal("系统管理员角色缺失".into()))?;
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
