//! 角色与权限点
use chrono::Utc;
use sea_orm::{
    ActiveModelTrait, ColumnTrait, DatabaseConnection, EntityTrait, PaginatorTrait, QueryFilter,
    QueryOrder, Set, TransactionTrait,
};
use serde::Deserialize;
use serde_json::{json, Value};

use crate::dto::{PageQuery, PageResp};
use crate::entity::enums::{CommonStatus, UserType};
use crate::entity::{permissions, role_permissions, roles, user_roles, users};
use crate::error::{ApiResult, AppError};
use crate::middleware::auth::CurrentUser;

use super::audit;

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RoleUpsert {
    pub name: String,
    pub description: Option<String>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PermAssign {
    pub permission_ids: Vec<u64>,
}

fn validate_details(req: &RoleUpsert) -> ApiResult<()> {
    if req.name.trim().is_empty() || req.name.trim().chars().count() > 64 {
        return Err(AppError::BadRequest("角色名称需为 1~64 个字符".into()));
    }
    if req
        .description
        .as_ref()
        .is_some_and(|v| v.chars().count() > 255)
    {
        return Err(AppError::BadRequest("角色说明不能超过 255 个字符".into()));
    }
    Ok(())
}

pub async fn perm_tree(db: &DatabaseConnection) -> ApiResult<Value> {
    let all = permissions::Entity::find()
        .order_by_asc(permissions::Column::SortNo)
        .order_by_asc(permissions::Column::Id)
        .all(db)
        .await?;
    Ok(json!(all
        .iter()
        .map(|p| json!({
            "id": p.id, "code": p.code, "name": p.name,
            "type": if p.perm_type == crate::entity::enums::PermissionType::Menu { "MENU" } else { "ACTION" },
            "parentId": p.parent_id, "sortNo": p.sort_no,
        }))
        .collect::<Vec<_>>()))
}

async fn role_json(db: &DatabaseConnection, r: &roles::Model) -> Value {
    let perm_ids: Vec<u64> = role_permissions::Entity::find()
        .filter(role_permissions::Column::RoleId.eq(r.id))
        .all(db)
        .await
        .map(|v| v.into_iter().map(|x| x.permission_id).collect())
        .unwrap_or_default();
    let assigned_user_count = user_roles::Entity::find()
        .filter(user_roles::Column::RoleId.eq(r.id))
        .count(db)
        .await
        .unwrap_or(0);
    json!({
        "id": r.id, "name": r.name, "description": r.description,
        "isBuiltIn": r.is_built_in,
        "status": if r.status == CommonStatus::Active { "ACTIVE" } else { "DISABLED" },
        "permissionIds": perm_ids,
        "assignedUserCount": assigned_user_count,
        "createdAt": r.created_at,
    })
}

pub async fn list(db: &DatabaseConnection, q: &PageQuery) -> ApiResult<PageResp<Value>> {
    let (page, size) = q.clamped();
    let paginator = roles::Entity::find()
        .order_by_asc(roles::Column::Id)
        .paginate(db, size);
    let total = paginator.num_items().await?;
    let items = paginator.fetch_page(page - 1).await?;
    let mut list = Vec::with_capacity(items.len());
    for r in &items {
        list.push(role_json(db, r).await);
    }
    Ok(PageResp::new(list, total, page, size))
}

pub async fn create(
    db: &DatabaseConnection,
    me: &CurrentUser,
    req: &RoleUpsert,
) -> ApiResult<Value> {
    validate_details(req)?;
    let now = Utc::now();
    let txn = db.begin().await?;
    let model = roles::ActiveModel {
        name: Set(req.name.trim().to_string()),
        description: Set(req.description.clone()),
        is_built_in: Set(false),
        status: Set(CommonStatus::Active),
        created_at: Set(now),
        updated_at: Set(now),
        ..Default::default()
    }
    .insert(&txn)
    .await?;
    audit::insert(
        &txn,
        Some(me.id),
        Some(me.employee_no.clone()),
        "ROLE_CREATE",
        Some("role"),
        Some(model.id.to_string()),
        Some(json!({
            "name": model.name,
            "status": "ACTIVE",
        })),
        None,
    )
    .await?;
    txn.commit().await?;
    Ok(role_json(db, &model).await)
}

pub async fn update(
    db: &DatabaseConnection,
    me: &CurrentUser,
    id: u64,
    req: &RoleUpsert,
) -> ApiResult<Value> {
    validate_details(req)?;
    let role = roles::Entity::find_by_id(id)
        .one(db)
        .await?
        .ok_or(AppError::NotFound)?;
    if role.is_built_in && req.name.trim() != role.name {
        return Err(AppError::BadRequest("内置角色名称不可修改".into()));
    }
    let old_name = role.name.clone();
    let old_description = role.description.clone();
    let txn = db.begin().await?;
    let mut am: roles::ActiveModel = role.into();
    am.name = Set(req.name.trim().to_string());
    am.description = Set(req.description.clone());
    am.updated_at = Set(Utc::now());
    let model = am.update(&txn).await?;
    audit::insert(
        &txn,
        Some(me.id),
        Some(me.employee_no.clone()),
        "ROLE_UPDATE",
        Some("role"),
        Some(id.to_string()),
        Some(json!({
            "oldName": old_name,
            "newName": model.name,
            "descriptionChanged": old_description != model.description,
        })),
        None,
    )
    .await?;
    txn.commit().await?;
    Ok(role_json(db, &model).await)
}

pub async fn set_status(
    db: &DatabaseConnection,
    me: &CurrentUser,
    id: u64,
    status: &str,
) -> ApiResult<Value> {
    let txn = db.begin().await?;
    super::perm::lock_management_state(&txn).await?;
    super::perm::recheck_manager(&txn, me.id, "role:manage").await?;
    let role = roles::Entity::find_by_id(id)
        .one(&txn)
        .await?
        .ok_or(AppError::NotFound)?;
    let st = match status {
        "ACTIVE" => CommonStatus::Active,
        "DISABLED" => CommonStatus::Disabled,
        _ => return Err(AppError::BadRequest("非法状态".into())),
    };
    if st == CommonStatus::Disabled {
        let bound_user_ids: Vec<u64> = user_roles::Entity::find()
            .filter(user_roles::Column::RoleId.eq(id))
            .all(&txn)
            .await?
            .into_iter()
            .map(|binding| binding.user_id)
            .collect();
        let active_users = if bound_user_ids.is_empty() {
            0
        } else {
            users::Entity::find()
                .filter(users::Column::Id.is_in(bound_user_ids))
                .filter(users::Column::UserType.eq(UserType::Internal))
                .filter(users::Column::Status.eq(CommonStatus::Active))
                .count(&txn)
                .await?
        };
        if active_users > 0 {
            return Err(AppError::BadRequest(format!(
                "该角色仍绑定 {active_users} 个启用用户，请先为这些用户更换角色或禁用账号"
            )));
        }
    }
    let old_status = role.status;
    let mut am: roles::ActiveModel = role.into();
    am.status = Set(st);
    am.updated_at = Set(Utc::now());
    let model = am.update(&txn).await?;
    audit::insert(
        &txn,
        Some(me.id),
        Some(me.employee_no.clone()),
        "ROLE_STATUS",
        Some("role"),
        Some(id.to_string()),
        Some(json!({
            "name": model.name,
            "oldStatus": if old_status == CommonStatus::Active { "ACTIVE" } else { "DISABLED" },
            "newStatus": status,
        })),
        None,
    )
    .await?;
    txn.commit().await?;
    Ok(role_json(db, &model).await)
}

pub async fn assign_permissions(
    db: &DatabaseConnection,
    me: &CurrentUser,
    id: u64,
    req: &PermAssign,
) -> ApiResult<()> {
    let txn = db.begin().await?;
    super::perm::lock_management_state(&txn).await?;
    super::perm::recheck_manager(&txn, me.id, "role:manage").await?;
    let role = roles::Entity::find_by_id(id)
        .one(&txn)
        .await?
        .ok_or(AppError::NotFound)?;
    if req.permission_ids.len() > 500 {
        return Err(AppError::BadRequest("权限点数量超过上限".into()));
    }
    // 先整批校验，再事务化重建——避免删除后才发现非法 id 留下空权限角色
    let mut permission_ids = req.permission_ids.clone();
    permission_ids.sort_unstable();
    permission_ids.dedup();
    let valid: std::collections::HashSet<u64> = permissions::Entity::find()
        .filter(permissions::Column::Id.is_in(permission_ids.clone()))
        .all(&txn)
        .await?
        .into_iter()
        .map(|p| p.id)
        .collect();
    for pid in &permission_ids {
        if !valid.contains(pid) {
            return Err(AppError::BadRequest(format!("权限点不存在: {pid}")));
        }
    }
    if role.is_built_in && role.name == "系统管理员" {
        let bound_user_ids: Vec<u64> = user_roles::Entity::find()
            .filter(user_roles::Column::RoleId.eq(id))
            .all(&txn)
            .await?
            .into_iter()
            .map(|binding| binding.user_id)
            .collect();
        let active_users = if bound_user_ids.is_empty() {
            0
        } else {
            users::Entity::find()
                .filter(users::Column::Id.is_in(bound_user_ids))
                .filter(users::Column::UserType.eq(UserType::Internal))
                .filter(users::Column::Status.eq(CommonStatus::Active))
                .count(&txn)
                .await?
        };
        if active_users > 0 {
            let required: std::collections::HashMap<String, u64> = permissions::Entity::find()
                .filter(permissions::Column::Code.is_in([
                    "rbac:role",
                    "role:manage",
                    "org:user",
                    "user:manage",
                ]))
                .all(&txn)
                .await?
                .into_iter()
                .map(|permission| (permission.code, permission.id))
                .collect();
            let missing = ["rbac:role", "role:manage", "org:user", "user:manage"]
                .iter()
                .any(|code| {
                    required
                        .get(*code)
                        .is_none_or(|id| !permission_ids.contains(id))
                });
            if missing {
                return Err(AppError::BadRequest(
                    "系统管理员角色绑定启用用户时，必须保留用户管理和角色管理权限".into(),
                ));
            }
        }
    }
    let old_permission_ids: Vec<u64> = role_permissions::Entity::find()
        .filter(role_permissions::Column::RoleId.eq(id))
        .all(&txn)
        .await?
        .into_iter()
        .map(|binding| binding.permission_id)
        .collect();
    role_permissions::Entity::delete_many()
        .filter(role_permissions::Column::RoleId.eq(id))
        .exec(&txn)
        .await?;
    for pid in &permission_ids {
        role_permissions::ActiveModel {
            role_id: Set(id),
            permission_id: Set(*pid),
        }
        .insert(&txn)
        .await?;
    }
    audit::insert(
        &txn,
        Some(me.id),
        Some(me.employee_no.clone()),
        "ROLE_ASSIGN_PERMS",
        Some("role"),
        Some(id.to_string()),
        Some(json!({
            "oldPermissionCount": old_permission_ids.len(),
            "newPermissionCount": permission_ids.len(),
            "oldPermissionIds": old_permission_ids,
            "newPermissionIds": permission_ids,
        })),
        None,
    )
    .await?;
    txn.commit().await?;
    Ok(())
}

pub async fn delete(db: &DatabaseConnection, me: &CurrentUser, id: u64) -> ApiResult<()> {
    let txn = db.begin().await?;
    super::perm::lock_management_state(&txn).await?;
    super::perm::recheck_manager(&txn, me.id, "role:delete").await?;
    let role = roles::Entity::find_by_id(id)
        .one(&txn)
        .await?
        .ok_or(AppError::NotFound)?;
    let assigned = user_roles::Entity::find()
        .filter(user_roles::Column::RoleId.eq(id))
        .count(&txn)
        .await?;
    if assigned > 0 {
        return Err(AppError::BadRequest(format!(
            "该角色仍绑定 {assigned} 个用户，请先为这些用户更换角色"
        )));
    }
    role_permissions::Entity::delete_many()
        .filter(role_permissions::Column::RoleId.eq(id))
        .exec(&txn)
        .await?;
    audit::insert(
        &txn,
        Some(me.id),
        Some(me.employee_no.clone()),
        "ROLE_DELETE",
        Some("role"),
        Some(id.to_string()),
        Some(json!({ "name": role.name })),
        None,
    )
    .await?;
    roles::Entity::delete_by_id(id).exec(&txn).await?;
    txn.commit().await?;
    Ok(())
}
