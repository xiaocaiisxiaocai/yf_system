//! 种子数据：admin 账号、4 个内置角色、权限点清单、角色-权限绑定、系统参数。
//! 首次建库时生成随机初始密码并仅打印一次部署日志；admin 首次登录强制改密。
use std::collections::HashMap;
use std::fmt::Write as _;

use argon2::password_hash::{
    rand_core::{OsRng, RngCore},
    SaltString,
};
use argon2::{Argon2, PasswordHasher};
use sea_orm_migration::prelude::*;
use sea_orm_migration::sea_orm::{ConnectionTrait, DbBackend, Statement};

pub struct Migration;

impl MigrationName for Migration {
    fn name(&self) -> &str {
        "m20260903_000002_seed"
    }
}

const MENUS: &[(&str, &str)] = &[
    ("dashboard", "工作台"),
    ("project:list", "项目列表"),
    ("supplier:list", "供应商管理"),
    ("org:user", "用户管理"),
    ("org:dept", "组织架构"),
    ("rbac:role", "角色权限"),
    ("log:audit", "操作日志"),
    ("system:config", "系统参数"),
];

/// (code, 名称, 所属菜单 code)
const ACTIONS: &[(&str, &str, &str)] = &[
    ("project:create", "创建项目", "project:list"),
    ("project:update", "编辑项目", "project:list"),
    ("project:delete", "删除项目", "project:list"),
    ("project:status", "项目状态变更", "project:list"),
    ("project:member", "管理项目成员", "project:list"),
    ("project:view_all", "查看全部项目", "project:list"),
    ("round:create", "创建轮次", "project:list"),
    ("round:confirm", "确认/驳回轮次", "project:list"),
    ("round:cancel", "撤销轮次", "project:list"),
    ("file:upload", "上传文件", "project:list"),
    ("file:download", "下载文件", "project:list"),
    ("file:preview", "预览文件", "project:list"),
    ("file:delete", "删除文件", "project:list"),
    ("message:create", "发表留言", "project:list"),
    ("message:delete_any", "删除留言", "project:list"),
    ("supplier:manage", "供应商管理", "supplier:list"),
    ("supplier:delete", "删除供应商", "supplier:list"),
    ("supplier:account", "供应商账号管理", "supplier:list"),
    ("supplier:account_delete", "删除供应商账号", "supplier:list"),
    ("user:manage", "用户管理", "org:user"),
    ("user:delete", "删除用户", "org:user"),
    ("dept:manage", "组织架构管理", "org:dept"),
    ("dept:delete", "删除组织节点", "org:dept"),
    ("role:manage", "角色管理", "rbac:role"),
    ("role:delete", "删除角色", "rbac:role"),
    ("log:view", "日志查看", "log:audit"),
    ("log:delete", "删除日志", "log:audit"),
    ("config:manage", "系统参数管理", "system:config"),
];

const ROLES: &[(&str, &str)] = &[
    ("ADMIN", "系统管理员"),
    ("PROJECT_MANAGER", "项目管理员"),
    ("STAFF", "内部成员"),
    ("SUPPLIER", "供应商人员"),
];

const PROJECT_MANAGER_PERMS: &[&str] = &[
    "dashboard",
    "project:list",
    "project:create",
    "project:update",
    "project:status",
    "project:member",
    "project:view_all",
    "round:create",
    "round:confirm",
    "round:cancel",
    "file:upload",
    "file:download",
    "file:preview",
    "message:create",
];

const STAFF_PERMS: &[&str] = &[
    "dashboard",
    "project:list",
    "round:confirm",
    "file:upload",
    "file:download",
    "file:preview",
    "message:create",
];

/// 供应商人员：固定最小权限集（数据范围=本供应商，应用层强制）
const SUPPLIER_PERMS: &[&str] = &[
    "dashboard",
    "project:list",
    "file:upload",
    "file:download",
    "file:preview",
    "message:create",
];

const CONFIGS: &[(&str, &str, &str)] = &[
    ("upload.max_file_size", "2147483648", "单文件大小上限（字节，默认 2GB）"),
    ("upload.chunk_size", "10485760", "分片大小（字节，默认 10MB）"),
    (
        "upload.allowed_exts",
        "step,stp,iges,igs,stl,obj,fbx,dwg,dxf,pdf,doc,docx,xls,xlsx,ppt,pptx,png,jpg,jpeg,zip,rar,7z",
        "允许上传的扩展名白名单",
    ),
    ("notify.enabled", "true", "邮件通知总开关"),
];

#[async_trait::async_trait]
impl MigrationTrait for Migration {
    async fn up(&self, m: &SchemaManager) -> Result<(), DbErr> {
        let db = m.get_connection();

        // 1. admin 账号。账号已存在时只跳过账号本身，后续角色/权限/参数仍需补齐。
        let existing = db
            .query_one(Statement::from_string(
                DbBackend::MySql,
                "SELECT id FROM users WHERE username = 'admin'".to_string(),
            ))
            .await?;
        if existing.is_none() {
            let mut random = [0u8; 16];
            OsRng.fill_bytes(&mut random);
            let mut suffix = String::with_capacity(32);
            for byte in random {
                write!(&mut suffix, "{byte:02x}").map_err(|e| DbErr::Custom(e.to_string()))?;
            }
            let initial_password = format!("Aa1!{suffix}");
            let salt = SaltString::generate(&mut OsRng);
            let hash = Argon2::default()
                .hash_password(initial_password.as_bytes(), &salt)
                .map_err(|e| DbErr::Custom(format!("argon2: {e}")))?
                .to_string();
            db.execute(Statement::from_sql_and_values(
                DbBackend::MySql,
                "INSERT INTO users (username, password_hash, real_name, email, user_type, status, must_change_password, failed_login_attempts, created_at, updated_at) \
                 VALUES (?, ?, ?, ?, 'INTERNAL', 'ACTIVE', 1, 0, NOW(3), NOW(3))",
                ["admin".into(), hash.into(), "系统管理员".into(), "admin@example.com".into()],
            ))
            .await?;
            tracing::warn!(username = "admin", password = %initial_password, "首次部署管理员凭据；请立即安全保存并在首次登录后修改");
        }

        // 2. 角色
        for (code, name) in ROLES {
            db.execute(Statement::from_sql_and_values(
                DbBackend::MySql,
                "INSERT IGNORE INTO roles (code, name, is_built_in, status, created_at, updated_at) VALUES (?, ?, 1, 'ACTIVE', NOW(3), NOW(3))",
                [(*code).into(), (*name).into()],
            ))
            .await?;
        }

        // 3. 菜单权限点
        for (i, (code, name)) in MENUS.iter().enumerate() {
            db.execute(Statement::from_sql_and_values(
                DbBackend::MySql,
                "INSERT IGNORE INTO permissions (code, name, type, parent_id, sort_no) VALUES (?, ?, 'MENU', NULL, ?)",
                [(*code).into(), (*name).into(), (i as i32).into()],
            ))
            .await?;
        }

        // 4. 操作权限点（parent 由菜单 code 反查）
        let menu_ids = id_map(db, "permissions", "code", "type = 'MENU'").await?;
        for (i, (code, name, parent)) in ACTIONS.iter().enumerate() {
            let parent_id = menu_ids
                .get(*parent)
                .copied()
                .ok_or_else(|| DbErr::Custom(format!("菜单权限点缺失: {parent}")))?;
            db.execute(Statement::from_sql_and_values(
                DbBackend::MySql,
                "INSERT IGNORE INTO permissions (code, name, type, parent_id, sort_no) VALUES (?, ?, 'ACTION', ?, ?)",
                [(*code).into(), (*name).into(), parent_id.into(), (i as i32).into()],
            ))
            .await?;
        }

        // 5. 角色-权限绑定
        let perm_ids = id_map(db, "permissions", "code", "").await?;
        let role_ids = id_map(db, "roles", "code", "").await?;
        let all_codes: Vec<&String> = perm_ids.keys().collect();
        for (code, _) in ROLES {
            let role_id = role_ids[*code];
            let codes: Vec<&str> = match *code {
                "ADMIN" => all_codes.iter().map(|s| s.as_str()).collect(),
                "PROJECT_MANAGER" => PROJECT_MANAGER_PERMS.to_vec(),
                "STAFF" => STAFF_PERMS.to_vec(),
                "SUPPLIER" => SUPPLIER_PERMS.to_vec(),
                _ => vec![],
            };
            for c in codes {
                let pid = perm_ids[c];
                db.execute(Statement::from_sql_and_values(
                    DbBackend::MySql,
                    "INSERT IGNORE INTO role_permissions (role_id, permission_id) VALUES (?, ?)",
                    [role_id.into(), pid.into()],
                ))
                .await?;
            }
        }

        // 6. admin 绑定 ADMIN 角色
        let admin_id = db
            .query_one(Statement::from_string(
                DbBackend::MySql,
                "SELECT id FROM users WHERE username = 'admin'".to_string(),
            ))
            .await?
            .and_then(|r| r.try_get::<u64>("", "id").ok())
            .ok_or_else(|| DbErr::Custom("admin 用户未找到".into()))?;
        db.execute(Statement::from_sql_and_values(
            DbBackend::MySql,
            "INSERT IGNORE INTO user_roles (user_id, role_id) VALUES (?, ?)",
            [admin_id.into(), role_ids["ADMIN"].into()],
        ))
        .await?;

        // 7. 系统参数
        for (k, v, desc) in CONFIGS {
            db.execute(Statement::from_sql_and_values(
                DbBackend::MySql,
                "INSERT IGNORE INTO system_configs (cfg_key, cfg_value, description) VALUES (?, ?, ?)",
                [(*k).into(), (*v).into(), (*desc).into()],
            ))
            .await?;
        }

        Ok(())
    }

    async fn down(&self, m: &SchemaManager) -> Result<(), DbErr> {
        let db = m.get_connection();
        // 种子可能已被业务使用或修改，回滚不得清空用户创建的数据；只移除仍保持默认值的配置项。
        for sql in [
            "DELETE FROM system_configs WHERE cfg_key = 'upload.max_file_size' AND cfg_value = '2147483648'",
            "DELETE FROM system_configs WHERE cfg_key = 'upload.chunk_size' AND cfg_value = '10485760'",
            "DELETE FROM system_configs WHERE cfg_key = 'upload.allowed_exts' AND cfg_value = 'step,stp,iges,igs,stl,obj,fbx,dwg,dxf,pdf,doc,docx,xls,xlsx,ppt,pptx,png,jpg,jpeg,zip,rar,7z'",
            "DELETE FROM system_configs WHERE cfg_key = 'notify.enabled' AND cfg_value = 'true'",
        ] {
            db.execute(Statement::from_string(DbBackend::MySql, sql.to_string()))
                .await?;
        }
        Ok(())
    }
}

/// 查表得 code→id 映射；cond 为空串表示无条件
async fn id_map(
    db: &SchemaManagerConnection<'_>,
    table: &str,
    key_col: &str,
    cond: &str,
) -> Result<HashMap<String, u64>, DbErr> {
    let sql = if cond.is_empty() {
        format!("SELECT id, {key_col} FROM {table}")
    } else {
        format!("SELECT id, {key_col} FROM {table} WHERE {cond}")
    };
    let rows = db
        .query_all(Statement::from_string(DbBackend::MySql, sql))
        .await?;
    let mut map = HashMap::new();
    for r in rows {
        let id = r.try_get::<u64>("", "id")?;
        let code = r.try_get::<String>("", key_col)?;
        map.insert(code, id);
    }
    Ok(map)
}
