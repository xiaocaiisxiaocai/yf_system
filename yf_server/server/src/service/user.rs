//! 内部用户管理（供应商人员账号走 service::supplier 的账号接口）
use chrono::Utc;
use sea_orm::{
    ActiveModelTrait, ColumnTrait, Condition, ConnectionTrait, DatabaseConnection, EntityTrait,
    PaginatorTrait, QueryFilter, QueryOrder, QuerySelect, Set, TransactionTrait,
};
use serde::Deserialize;
use serde_json::{json, Value};

use crate::dto::PageResp;
use crate::entity::enums::{CommonStatus, UserType};
use crate::entity::{
    audit_logs, departments, email_outbox, files, message_reads, messages, project_members,
    project_status_logs, projects, refresh_tokens, roles, suppliers, upload_sessions, user_roles,
    users,
};
use crate::error::{ApiResult, AppError};
use crate::middleware::auth::CurrentUser;
use crate::util::password;

use super::{audit, auth as auth_service, dept};

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct UserListQuery {
    #[serde(default = "crate::dto::default_page")]
    pub page: u64,
    #[serde(default = "crate::dto::default_page_size")]
    pub page_size: u64,
    pub keyword: Option<String>,
    pub department_id: Option<u64>,
    pub status: Option<String>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RoleOptionQuery {
    pub keyword: Option<String>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct UserCreate {
    pub employee_no: String,
    pub password: String,
    pub real_name: String,
    pub email: String,
    pub department_id: Option<u64>,
    /// 单角色契约；role_ids 仅用于兼容旧客户端。
    pub role_id: Option<u64>,
    pub role_ids: Option<Vec<u64>>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct UserUpdate {
    pub real_name: Option<String>,
    pub email: Option<String>,
    #[serde(default, deserialize_with = "deserialize_optional_department")]
    pub department_id: Option<Option<u64>>,
    /// 传了就在同一事务里重建角色绑定，避免用户资料/角色两次 PUT 的半失败
    pub role_id: Option<u64>,
    pub role_ids: Option<Vec<u64>>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PasswordReset {
    pub new_password: String,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RoleAssign {
    pub role_ids: Vec<u64>,
}

fn validate_role_ids(role_ids: &[u64]) -> ApiResult<()> {
    let distinct: std::collections::HashSet<u64> = role_ids.iter().copied().collect();
    if distinct.len() != 1 {
        return Err(AppError::BadRequest(
            "启用的内部用户必须且只能绑定一个角色".into(),
        ));
    }
    Ok(())
}

fn deserialize_optional_department<'de, D>(deserializer: D) -> Result<Option<Option<u64>>, D::Error>
where
    D: serde::Deserializer<'de>,
{
    Option::<u64>::deserialize(deserializer).map(Some)
}

fn requested_role_ids(
    role_id: Option<u64>,
    legacy_role_ids: Option<&[u64]>,
) -> ApiResult<Option<Vec<u64>>> {
    match (role_id, legacy_role_ids) {
        (None, None) => Ok(None),
        (Some(id), None) => Ok(Some(vec![id])),
        (None, Some(ids)) => {
            validate_role_ids(ids)?;
            Ok(Some(vec![ids[0]]))
        }
        (Some(id), Some(ids)) => {
            validate_role_ids(ids)?;
            if ids.iter().any(|legacy_id| *legacy_id != id) {
                return Err(AppError::BadRequest("roleId 与 roleIds 不一致".into()));
            }
            Ok(Some(vec![id]))
        }
    }
}

pub async fn to_json(db: &DatabaseConnection, u: &users::Model) -> Value {
    let dept_name = dept::name_of(db, u.department_id).await;
    let role_ids: Vec<u64> = user_roles::Entity::find()
        .filter(user_roles::Column::UserId.eq(u.id))
        .all(db)
        .await
        .map(|v| v.into_iter().map(|r| r.role_id).collect())
        .unwrap_or_default();
    // 批量取角色名，避免逐角色查询
    let role_map = role_name_map(db, &role_ids).await;
    let role_names: Vec<String> = role_ids
        .iter()
        .filter_map(|rid| role_map.get(rid).cloned())
        .collect();
    let role_id = role_ids.first().copied();
    let role_name = role_id.and_then(|rid| role_map.get(&rid).cloned());
    json!({
        "id": u.id, "employeeNo": u.employee_no, "realName": u.real_name, "email": u.email,
        "userType": u.user_type.as_str(), "supplierId": u.supplier_id,
        "departmentId": u.department_id, "departmentName": dept_name,
        "status": if u.status == CommonStatus::Active { "ACTIVE" } else { "DISABLED" },
        "mustChangePassword": u.must_change_password,
        "lastLoginAt": u.last_login_at, "createdAt": u.created_at,
        "roleId": role_id, "roleName": role_name,
        "roleIds": role_ids, "roleNames": role_names,
    })
}

async fn role_name_map(
    db: &impl ConnectionTrait,
    ids: &[u64],
) -> std::collections::HashMap<u64, String> {
    if ids.is_empty() {
        return Default::default();
    }
    roles::Entity::find()
        .filter(roles::Column::Id.is_in(ids.to_vec()))
        .all(db)
        .await
        .unwrap_or_default()
        .into_iter()
        .map(|r| (r.id, r.name))
        .collect()
}

pub async fn list(db: &DatabaseConnection, q: &UserListQuery) -> ApiResult<PageResp<Value>> {
    let (page, size) = crate::dto::clamp_page(q.page, q.page_size);
    let mut cond = Condition::all().add(users::Column::UserType.eq(UserType::Internal));
    if let Some(kw) = q.keyword.as_ref().filter(|k| !k.trim().is_empty()) {
        cond = cond.add(
            Condition::any()
                .add(users::Column::EmployeeNo.contains(kw.trim()))
                .add(users::Column::RealName.contains(kw.trim()))
                .add(users::Column::Email.contains(kw.trim())),
        );
    }
    if let Some(d) = q.department_id {
        cond = cond.add(users::Column::DepartmentId.eq(d));
    }
    if let Some(s) = &q.status {
        cond = cond.add(users::Column::Status.eq(s.as_str()));
    }
    let paginator = users::Entity::find()
        .filter(cond)
        .order_by_desc(users::Column::Id)
        .paginate(db, size);
    let total = paginator.num_items().await?;
    let items = paginator.fetch_page(page - 1).await?;
    // 批量预取部门/角色绑定/角色名，避免每行多次查询的 N+1
    let page_ids: Vec<u64> = items.iter().map(|u| u.id).collect();
    let dept_map: std::collections::HashMap<u64, String> = departments::Entity::find()
        .filter(
            departments::Column::Id.is_in(
                items
                    .iter()
                    .filter_map(|u| u.department_id)
                    .collect::<Vec<_>>(),
            ),
        )
        .all(db)
        .await?
        .into_iter()
        .map(|d| (d.id, d.name))
        .collect();
    let bindings = user_roles::Entity::find()
        .filter(user_roles::Column::UserId.is_in(page_ids))
        .all(db)
        .await?;
    let all_role_ids: Vec<u64> = bindings.iter().map(|b| b.role_id).collect();
    let role_map = role_name_map(db, &all_role_ids).await;
    let mut user_role_map: std::collections::HashMap<u64, Vec<u64>> = Default::default();
    for b in bindings {
        user_role_map.entry(b.user_id).or_default().push(b.role_id);
    }
    let mut list = Vec::with_capacity(items.len());
    for u in &items {
        let role_ids = user_role_map.get(&u.id).cloned().unwrap_or_default();
        let role_names: Vec<String> = role_ids
            .iter()
            .filter_map(|rid| role_map.get(rid).cloned())
            .collect();
        let role_id = role_ids.first().copied();
        let role_name = role_id.and_then(|rid| role_map.get(&rid).cloned());
        list.push(json!({
            "id": u.id, "employeeNo": u.employee_no, "realName": u.real_name, "email": u.email,
            "userType": u.user_type.as_str(), "supplierId": u.supplier_id,
            "departmentId": u.department_id,
            "departmentName": u.department_id.and_then(|d| dept_map.get(&d)),
            "status": if u.status == CommonStatus::Active { "ACTIVE" } else { "DISABLED" },
            "mustChangePassword": u.must_change_password,
            "lastLoginAt": u.last_login_at, "createdAt": u.created_at,
            "roleId": role_id, "roleName": role_name,
            "roleIds": role_ids, "roleNames": role_names,
        }));
    }
    Ok(PageResp::new(list, total, page, size))
}

pub async fn role_options(db: &DatabaseConnection, q: &RoleOptionQuery) -> ApiResult<Value> {
    let mut cond = Condition::all()
        .add(roles::Column::Status.eq(CommonStatus::Active))
        .add(roles::Column::Name.ne("供应商人员"));
    if let Some(keyword) = q
        .keyword
        .as_ref()
        .map(|value| value.trim())
        .filter(|value| !value.is_empty())
    {
        cond = cond.add(roles::Column::Name.contains(keyword));
    }
    let items = roles::Entity::find()
        .filter(cond)
        .order_by_asc(roles::Column::Id)
        .all(db)
        .await?;
    Ok(json!(items
        .into_iter()
        .map(|role| json!({ "id": role.id, "name": role.name }))
        .collect::<Vec<_>>()))
}

pub async fn create(
    db: &DatabaseConnection,
    me: &CurrentUser,
    req: &UserCreate,
) -> ApiResult<Value> {
    let txn = db.begin().await?;
    super::perm::lock_management_state(&txn).await?;
    super::perm::recheck_manager(&txn, me.id, "user:manage").await?;
    let employee_no = req.employee_no.trim();
    crate::util::validation::employee_no(employee_no)?;
    if req.real_name.trim().is_empty() || req.real_name.trim().chars().count() > 32 {
        return Err(AppError::BadRequest("姓名需为 1~32 个字符".into()));
    }
    if users::Entity::find()
        .filter(users::Column::EmployeeNo.eq(employee_no))
        .one(&txn)
        .await?
        .is_some()
    {
        return Err(AppError::Conflict("工号已存在".into()));
    }
    if !password::strong_enough(&req.password) {
        return Err(AppError::BadRequest(password::POLICY_MESSAGE.into()));
    }
    crate::util::validation::email(&req.email)?;
    if let Some(d) = req.department_id {
        dept::ensure_active(&txn, d).await?;
    }
    let role_ids = requested_role_ids(req.role_id, req.role_ids.as_deref())?
        .ok_or_else(|| AppError::BadRequest("请选择角色".into()))?;
    super::perm::ensure_manage_role(&txn, me.id, role_ids[0]).await?;
    let now = Utc::now();
    // 用户与角色绑定同事务，避免半成功状态
    let model = users::ActiveModel {
        employee_no: Set(employee_no.to_string()),
        password_hash: Set(password::hash(&req.password)?),
        real_name: Set(req.real_name.trim().to_string()),
        email: Set(req.email.trim().to_string()),
        user_type: Set(UserType::Internal),
        supplier_id: Set(None),
        department_id: Set(req.department_id),
        status: Set(CommonStatus::Active),
        must_change_password: Set(true),
        failed_login_attempts: Set(0),
        locked_until: Set(None),
        last_login_at: Set(None),
        last_login_ip: Set(None),
        created_by: Set(Some(me.id)),
        created_at: Set(now),
        updated_at: Set(now),
        ..Default::default()
    }
    .insert(&txn)
    .await
    .map_err(|error| crate::error::unique_conflict(error, "工号已存在"))?;
    let role = bind_internal_role(&txn, model.id, role_ids[0]).await?;
    audit::insert(
        &txn,
        Some(me.id),
        Some(me.employee_no.clone()),
        "USER_CREATE",
        Some("user"),
        Some(model.id.to_string()),
        Some(json!({
            "employeeNo": employee_no,
            "departmentId": req.department_id,
            "roleId": role.id,
            "roleName": role.name,
        })),
        None,
    )
    .await?;
    txn.commit().await?;
    Ok(to_json(db, &model).await)
}

async fn ensure_internal_role_assignable(
    db: &impl ConnectionTrait,
    role_id: u64,
) -> ApiResult<roles::Model> {
    let role = roles::Entity::find_by_id(role_id)
        .one(db)
        .await?
        .ok_or_else(|| AppError::BadRequest(format!("角色不存在: {role_id}")))?;
    if role.status != CommonStatus::Active {
        return Err(AppError::BadRequest("不能绑定已禁用的角色".into()));
    }
    if role.is_built_in && role.name == "供应商人员" {
        return Err(AppError::BadRequest(
            "供应商角色只能由供应商账号使用".into(),
        ));
    }
    Ok(role)
}

async fn bind_internal_role(
    db: &impl ConnectionTrait,
    user_id: u64,
    role_id: u64,
) -> ApiResult<roles::Model> {
    let role = ensure_internal_role_assignable(db, role_id).await?;
    user_roles::ActiveModel {
        user_id: Set(user_id),
        role_id: Set(role.id),
    }
    .insert(db)
    .await?;
    Ok(role)
}

/// 防止管理员误删自己的 ADMIN 绑定，或移除系统中最后一个启用管理员。
async fn ensure_admin_role_change_safe(
    db: &impl ConnectionTrait,
    actor_id: u64,
    target_id: u64,
    new_role_ids: &[u64],
) -> ApiResult<()> {
    let Some(admin_role) = roles::Entity::find()
        .filter(roles::Column::IsBuiltIn.eq(true))
        .filter(roles::Column::Name.eq("系统管理员"))
        .one(db)
        .await?
    else {
        return Ok(());
    };
    let currently_admin = user_roles::Entity::find_by_id((target_id, admin_role.id))
        .one(db)
        .await?
        .is_some();
    if !currently_admin || new_role_ids.contains(&admin_role.id) {
        return Ok(());
    }
    if actor_id == target_id {
        return Err(AppError::BadRequest("不能移除自己的系统管理员角色".into()));
    }
    let target = users::Entity::find_by_id(target_id)
        .one(db)
        .await?
        .ok_or(AppError::NotFound)?;
    if target.status != CommonStatus::Active {
        return Ok(());
    }
    let bindings = user_roles::Entity::find()
        .filter(user_roles::Column::RoleId.eq(admin_role.id))
        .all(db)
        .await?;
    let admin_ids: Vec<u64> = bindings.into_iter().map(|b| b.user_id).collect();
    let active_admins = users::Entity::find()
        .filter(users::Column::Id.is_in(admin_ids))
        .filter(users::Column::Status.eq(CommonStatus::Active))
        .all(db)
        .await?
        .len();
    if active_admins <= 1 {
        return Err(AppError::BadRequest(
            "不能移除系统中最后一个启用管理员".into(),
        ));
    }
    Ok(())
}

pub async fn update(
    db: &DatabaseConnection,
    me: &CurrentUser,
    id: u64,
    req: &UserUpdate,
) -> ApiResult<Value> {
    let txn = db.begin().await?;
    super::perm::lock_management_state(&txn).await?;
    super::perm::recheck_manager(&txn, me.id, "user:manage").await?;
    let user = users::Entity::find_by_id(id)
        .one(&txn)
        .await?
        .ok_or(AppError::NotFound)?;
    if user.user_type != UserType::Internal {
        return Err(AppError::BadRequest("供应商人员请在供应商模块维护".into()));
    }
    super::perm::ensure_manage_user(&txn, me.id, id).await?;
    if let Some(v) = &req.real_name {
        if v.trim().is_empty() || v.trim().chars().count() > 32 {
            return Err(AppError::BadRequest("姓名需为 1~32 个字符".into()));
        }
    }
    if let Some(v) = &req.email {
        crate::util::validation::email(v)?;
    }
    if let Some(Some(d)) = req.department_id {
        dept::ensure_active(&txn, d).await?;
    }
    let requested_roles = requested_role_ids(req.role_id, req.role_ids.as_deref())?;
    let old_role_ids: Vec<u64> = user_roles::Entity::find()
        .filter(user_roles::Column::UserId.eq(id))
        .all(&txn)
        .await?
        .into_iter()
        .map(|binding| binding.role_id)
        .collect();
    validate_role_ids(&old_role_ids)?;
    let role_changed = requested_roles
        .as_ref()
        .is_some_and(|role_ids| role_ids != &old_role_ids);
    if role_changed {
        let role_ids = requested_roles
            .as_ref()
            .expect("role_changed requires requested roles");
        ensure_admin_role_change_safe(&txn, me.id, id, role_ids).await?;
        super::perm::ensure_manage_role(&txn, me.id, role_ids[0]).await?;
        ensure_internal_role_assignable(&txn, role_ids[0]).await?;
    } else if user.status == CommonStatus::Active {
        ensure_internal_role_assignable(&txn, old_role_ids[0]).await?;
    }
    let old_role_id = old_role_ids.first().copied();
    let old_department_id = user.department_id;
    // 资料与角色绑定（如提供）同一事务提交
    let mut am: users::ActiveModel = user.into();
    if let Some(v) = &req.real_name {
        am.real_name = Set(v.trim().to_string());
    }
    if let Some(v) = &req.email {
        am.email = Set(v.trim().to_string());
    }
    if let Some(department_id) = req.department_id {
        am.department_id = Set(department_id);
    }
    am.updated_at = Set(Utc::now());
    let model = am.update(&txn).await?;
    let mut new_role = None;
    if role_changed {
        let role_ids = requested_roles
            .as_ref()
            .expect("role_changed requires requested roles");
        user_roles::Entity::delete_many()
            .filter(user_roles::Column::UserId.eq(id))
            .exec(&txn)
            .await?;
        new_role = Some(bind_internal_role(&txn, id, role_ids[0]).await?);
    }
    let mut changed_fields = Vec::new();
    if req.real_name.is_some() {
        changed_fields.push("realName");
    }
    if req.email.is_some() {
        changed_fields.push("email");
    }
    if req.department_id.is_some() {
        changed_fields.push("departmentId");
    }
    if role_changed {
        changed_fields.push("roleId");
    }
    audit::insert(
        &txn,
        Some(me.id),
        Some(me.employee_no.clone()),
        "USER_UPDATE",
        Some("user"),
        Some(id.to_string()),
        Some(json!({
            "employeeNo": model.employee_no,
            "changedFields": changed_fields,
            "oldDepartmentId": old_department_id,
            "newDepartmentId": model.department_id,
            "oldRoleId": old_role_id,
            "newRoleId": new_role.as_ref().map(|role| role.id).or(old_role_id),
            "newRoleName": new_role.as_ref().map(|role| role.name.clone()),
        })),
        None,
    )
    .await?;
    txn.commit().await?;
    Ok(to_json(db, &model).await)
}

pub async fn set_status(
    db: &DatabaseConnection,
    me: &CurrentUser,
    id: u64,
    status: &str,
) -> ApiResult<Value> {
    let txn = db.begin().await?;
    super::perm::lock_management_state(&txn).await?;
    super::perm::recheck_manager(&txn, me.id, "user:manage").await?;
    if id == me.id {
        return Err(AppError::BadRequest("不能禁用自己的账号".into()));
    }
    let user = users::Entity::find_by_id(id)
        .one(&txn)
        .await?
        .ok_or(AppError::NotFound)?;
    if user.user_type != UserType::Internal {
        return Err(AppError::BadRequest("供应商人员请在供应商模块维护".into()));
    }
    super::perm::ensure_manage_user(&txn, me.id, id).await?;
    if status == "DISABLED" {
        ensure_admin_role_change_safe(&txn, me.id, id, &[]).await?;
    }
    let st = match status {
        "ACTIVE" => CommonStatus::Active,
        "DISABLED" => CommonStatus::Disabled,
        _ => return Err(AppError::BadRequest("非法状态".into())),
    };
    if st == CommonStatus::Active && user.user_type == UserType::Internal {
        let role_ids: Vec<u64> = user_roles::Entity::find()
            .filter(user_roles::Column::UserId.eq(id))
            .all(&txn)
            .await?
            .into_iter()
            .map(|binding| binding.role_id)
            .collect();
        validate_role_ids(&role_ids)?;
        ensure_internal_role_assignable(&txn, role_ids[0]).await?;
    }
    let old_status = user.status;
    let employee_no = user.employee_no.clone();
    let mut am: users::ActiveModel = user.into();
    am.status = Set(st);
    am.updated_at = Set(Utc::now());
    let model = am.update(&txn).await?;
    if st == CommonStatus::Disabled {
        auth_service::revoke_all(&txn, id).await?;
    }
    audit::insert(
        &txn,
        Some(me.id),
        Some(me.employee_no.clone()),
        "USER_STATUS",
        Some("user"),
        Some(id.to_string()),
        Some(json!({
            "employeeNo": employee_no,
            "oldStatus": if old_status == CommonStatus::Active { "ACTIVE" } else { "DISABLED" },
            "newStatus": status,
        })),
        None,
    )
    .await?;
    txn.commit().await?;
    Ok(to_json(db, &model).await)
}

pub async fn reset_password(
    db: &DatabaseConnection,
    me: &CurrentUser,
    id: u64,
    req: &PasswordReset,
) -> ApiResult<()> {
    if !password::strong_enough(&req.new_password) {
        return Err(AppError::BadRequest(password::POLICY_MESSAGE.into()));
    }
    let txn = db.begin().await?;
    super::perm::lock_management_state(&txn).await?;
    super::perm::recheck_manager(&txn, me.id, "user:manage").await?;
    let user = users::Entity::find_by_id(id)
        .lock_exclusive()
        .one(&txn)
        .await?
        .ok_or(AppError::NotFound)?;
    if user.user_type != UserType::Internal {
        return Err(AppError::BadRequest("供应商人员请在供应商模块维护".into()));
    }
    super::perm::ensure_manage_user(&txn, me.id, id).await?;
    let employee_no = user.employee_no.clone();
    let mut am: users::ActiveModel = user.into();
    am.password_hash = Set(password::hash(&req.new_password)?);
    am.must_change_password = Set(true);
    am.failed_login_attempts = Set(0);
    am.locked_until = Set(None);
    am.updated_at = Set(Utc::now());
    am.update(&txn).await?;
    auth_service::revoke_all(&txn, id).await?;
    audit::insert(
        &txn,
        Some(me.id),
        Some(me.employee_no.clone()),
        "USER_RESET_PASSWORD",
        Some("user"),
        Some(id.to_string()),
        Some(json!({
            "employeeNo": employee_no,
            "sessionsRevoked": true,
            "mustChangePassword": true,
        })),
        None,
    )
    .await?;
    txn.commit().await?;
    Ok(())
}

pub async fn assign_roles(
    db: &DatabaseConnection,
    me: &CurrentUser,
    id: u64,
    req: &RoleAssign,
) -> ApiResult<()> {
    let txn = db.begin().await?;
    super::perm::lock_management_state(&txn).await?;
    super::perm::recheck_manager(&txn, me.id, "user:manage").await?;
    let user = users::Entity::find_by_id(id)
        .one(&txn)
        .await?
        .ok_or(AppError::NotFound)?;
    if user.user_type != UserType::Internal {
        return Err(AppError::BadRequest("供应商人员角色固定，不可调整".into()));
    }
    super::perm::ensure_manage_user(&txn, me.id, id).await?;
    validate_role_ids(&req.role_ids)?;
    super::perm::ensure_manage_role(&txn, me.id, req.role_ids[0]).await?;
    ensure_admin_role_change_safe(&txn, me.id, id, &req.role_ids).await?;
    let old_role_id = user_roles::Entity::find()
        .filter(user_roles::Column::UserId.eq(id))
        .one(&txn)
        .await?
        .map(|binding| binding.role_id);
    user_roles::Entity::delete_many()
        .filter(user_roles::Column::UserId.eq(id))
        .exec(&txn)
        .await?;
    let role = bind_internal_role(&txn, id, req.role_ids[0]).await?;
    audit::insert(
        &txn,
        Some(me.id),
        Some(me.employee_no.clone()),
        "USER_ASSIGN_ROLE",
        Some("user"),
        Some(id.to_string()),
        Some(json!({
            "employeeNo": user.employee_no,
            "oldRoleId": old_role_id,
            "newRoleId": role.id,
            "newRoleName": role.name,
        })),
        None,
    )
    .await?;
    txn.commit().await?;
    Ok(())
}

pub(super) async fn ensure_no_owned_content(db: &impl ConnectionTrait, id: u64) -> ApiResult<()> {
    let has_file = files::Entity::find()
        .filter(files::Column::UploaderId.eq(id))
        .one(db)
        .await?
        .is_some();
    let has_message_history = messages::Entity::find()
        .filter(
            Condition::any()
                .add(messages::Column::SenderId.eq(id))
                .add(messages::Column::DeletedBy.eq(id)),
        )
        .one(db)
        .await?
        .is_some();
    let has_upload = upload_sessions::Entity::find()
        .filter(upload_sessions::Column::UploaderId.eq(id))
        .one(db)
        .await?
        .is_some();
    let has_history = projects::Entity::find()
        .filter(projects::Column::CreatedBy.eq(id))
        .one(db)
        .await?
        .is_some()
        || project_status_logs::Entity::find()
            .filter(project_status_logs::Column::OperatorId.eq(id))
            .one(db)
            .await?
            .is_some()
        || project_members::Entity::find()
            .filter(project_members::Column::CreatedBy.eq(id))
            .one(db)
            .await?
            .is_some()
        || message_reads::Entity::find()
            .filter(message_reads::Column::UserId.eq(id))
            .one(db)
            .await?
            .is_some()
        || suppliers::Entity::find()
            .filter(suppliers::Column::CreatedBy.eq(id))
            .one(db)
            .await?
            .is_some()
        || users::Entity::find()
            .filter(users::Column::CreatedBy.eq(id))
            .one(db)
            .await?
            .is_some()
        || email_outbox::Entity::find()
            .filter(email_outbox::Column::RecipientUserId.eq(id))
            .one(db)
            .await?
            .is_some()
        || audit_logs::Entity::find()
            .filter(audit_logs::Column::UserId.eq(id))
            .one(db)
            .await?
            .is_some();
    if has_file || has_message_history || has_upload || has_history {
        return Err(AppError::BadRequest(
            "该账号仍有业务或历史记录，请禁用账号，不要删除".into(),
        ));
    }
    Ok(())
}

pub(super) async fn remove_login_bindings(db: &impl ConnectionTrait, id: u64) -> ApiResult<()> {
    user_roles::Entity::delete_many()
        .filter(user_roles::Column::UserId.eq(id))
        .exec(db)
        .await?;
    refresh_tokens::Entity::delete_many()
        .filter(refresh_tokens::Column::UserId.eq(id))
        .exec(db)
        .await?;
    project_members::Entity::delete_many()
        .filter(project_members::Column::UserId.eq(id))
        .exec(db)
        .await?;
    users::Entity::delete_by_id(id).exec(db).await?;
    Ok(())
}

pub async fn delete(db: &DatabaseConnection, me: &CurrentUser, id: u64) -> ApiResult<()> {
    let txn = db.begin().await?;
    super::perm::lock_management_state(&txn).await?;
    let user = users::Entity::find_by_id(id)
        .lock_exclusive()
        .one(&txn)
        .await?
        .ok_or(AppError::NotFound)?;
    super::perm::recheck_manager(&txn, me.id, "user:delete").await?;
    if id == me.id {
        return Err(AppError::BadRequest("不能删除自己的账号".into()));
    }
    if user.user_type != UserType::Internal {
        return Err(AppError::BadRequest("供应商人员请在供应商模块删除".into()));
    }
    super::perm::ensure_manage_user(&txn, me.id, id).await?;
    if user.employee_no == "admin" {
        return Err(AppError::BadRequest("系统管理员账号不可删除".into()));
    }
    ensure_admin_role_change_safe(&txn, me.id, id, &[]).await?;
    ensure_no_owned_content(&txn, id).await?;
    let employee_no = user.employee_no.clone();
    audit::insert(
        &txn,
        Some(me.id),
        Some(me.employee_no.clone()),
        "USER_DELETE",
        Some("user"),
        Some(id.to_string()),
        Some(json!({ "employeeNo": employee_no })),
        None,
    )
    .await?;
    remove_login_bindings(&txn, id).await?;
    txn.commit().await?;
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::{requested_role_ids, validate_role_ids};

    #[test]
    fn active_internal_user_requires_exactly_one_distinct_role() {
        assert!(validate_role_ids(&[]).is_err());
        assert!(validate_role_ids(&[7]).is_ok());
        assert!(validate_role_ids(&[7, 7]).is_ok());

        let err = validate_role_ids(&[7, 8]).expect_err("多个不同角色必须被拒绝");
        assert!(err.to_string().contains("必须且只能"));
    }

    #[test]
    fn singular_and_legacy_role_contracts_are_compatible_but_must_agree() {
        assert_eq!(requested_role_ids(Some(7), None).unwrap(), Some(vec![7]));
        assert_eq!(requested_role_ids(None, Some(&[7])).unwrap(), Some(vec![7]));
        assert_eq!(
            requested_role_ids(Some(7), Some(&[7, 7])).unwrap(),
            Some(vec![7])
        );
        assert!(requested_role_ids(Some(7), Some(&[8])).is_err());
        assert!(requested_role_ids(None, Some(&[])).is_err());
    }
}
