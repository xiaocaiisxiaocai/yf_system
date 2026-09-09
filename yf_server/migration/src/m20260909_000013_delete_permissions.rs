//! Add independently assignable permissions for destructive actions.
use sea_orm_migration::prelude::*;
use sea_orm_migration::sea_orm::{ConnectionTrait, DbBackend, Statement};

pub struct Migration;

impl MigrationName for Migration {
    fn name(&self) -> &str {
        "m20260909_000013_delete_permissions"
    }
}

const DELETE_PERMISSIONS: &[(&str, &str, &str, i32)] = &[
    ("project:delete", "删除项目", "project:list", 90),
    ("file:delete", "删除文件", "project:list", 91),
    ("supplier:delete", "删除供应商", "supplier:list", 90),
    (
        "supplier:account_delete",
        "删除供应商账号",
        "supplier:list",
        91,
    ),
    ("user:delete", "删除用户", "org:user", 90),
    ("dept:delete", "删除组织节点", "org:dept", 90),
    ("role:delete", "删除角色", "rbac:role", 90),
    ("log:delete", "删除日志", "log:audit", 90),
];

#[async_trait::async_trait]
impl MigrationTrait for Migration {
    async fn up(&self, manager: &SchemaManager) -> Result<(), DbErr> {
        let db = manager.get_connection();
        for (code, name, parent_code, sort_no) in DELETE_PERMISSIONS {
            db.execute(Statement::from_sql_and_values(
                DbBackend::MySql,
                "INSERT IGNORE INTO permissions (code, name, type, parent_id, sort_no) \
                 SELECT ?, ?, 'ACTION', id, ? FROM permissions WHERE code = ? AND type = 'MENU'",
                [
                    (*code).into(),
                    (*name).into(),
                    (*sort_no).into(),
                    (*parent_code).into(),
                ],
            ))
            .await?;
        }

        // Preserve the pre-upgrade behavior: destructive permissions initially belong only to ADMIN.
        db.execute(Statement::from_string(
            DbBackend::MySql,
            "INSERT IGNORE INTO role_permissions (role_id, permission_id) \
             SELECT r.id, p.id FROM roles r CROSS JOIN permissions p \
             WHERE r.is_built_in = TRUE AND r.name = '系统管理员' \
               AND p.code IN ('project:delete','file:delete','supplier:delete',\
                              'supplier:account_delete','user:delete','dept:delete',\
                              'role:delete','log:delete')"
                .to_string(),
        ))
        .await?;

        // Stable lock row used by permission-sensitive transactions even if built-in roles are removed.
        db.execute(Statement::from_string(
            DbBackend::MySql,
            "INSERT IGNORE INTO system_configs (cfg_key, cfg_value, description) \
             VALUES ('security.management_lock', '1', '权限与高风险操作事务锁')"
                .to_string(),
        ))
        .await?;
        Ok(())
    }

    async fn down(&self, manager: &SchemaManager) -> Result<(), DbErr> {
        let db = manager.get_connection();
        db.execute(Statement::from_string(
            DbBackend::MySql,
            "DELETE rp FROM role_permissions rp JOIN permissions p ON p.id = rp.permission_id \
             WHERE p.code IN ('project:delete','file:delete','supplier:delete',\
                              'supplier:account_delete','user:delete','dept:delete',\
                              'role:delete','log:delete')"
                .to_string(),
        ))
        .await?;
        db.execute(Statement::from_string(
            DbBackend::MySql,
            "DELETE FROM permissions WHERE code IN ('project:delete','file:delete','supplier:delete',\
             'supplier:account_delete','user:delete','dept:delete','role:delete','log:delete')"
                .to_string(),
        ))
        .await?;
        db.execute(Statement::from_string(
            DbBackend::MySql,
            "DELETE FROM system_configs WHERE cfg_key = 'security.management_lock'".to_string(),
        ))
        .await?;
        Ok(())
    }
}
