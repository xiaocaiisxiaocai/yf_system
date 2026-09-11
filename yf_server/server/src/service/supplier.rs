//! 供应商及其人员账号
use chrono::Utc;
use sea_orm::{
    ActiveModelTrait, ColumnTrait, Condition, ConnectionTrait, DatabaseConnection, EntityTrait,
    PaginatorTrait, QueryFilter, QueryOrder, QuerySelect, Set, TransactionTrait,
};
use serde::Deserialize;
use serde_json::{json, Value};

use crate::dto::PageResp;
use crate::entity::enums::{CommonStatus, UserType};
use crate::entity::{projects, roles, suppliers, user_roles, users};
use crate::error::{ApiResult, AppError};
use crate::middleware::auth::CurrentUser;
use crate::util::password;

use super::{audit, auth as auth_service};

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SupplierListQuery {
    #[serde(default = "crate::dto::default_page")]
    pub page: u64,
    #[serde(default = "crate::dto::default_page_size")]
    pub page_size: u64,
    pub keyword: Option<String>,
    pub status: Option<String>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SupplierUpsert {
    pub name: String,
    pub remark: Option<String>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AccountCreate {
    pub employee_no: String,
    pub password: String,
    pub real_name: String,
    pub email: String,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AccountUpdate {
    pub real_name: Option<String>,
    pub email: Option<String>,
}

fn supplier_json(s: &suppliers::Model) -> Value {
    json!({
        "id": s.id, "name": s.name, "remark": s.remark,
        "status": if s.status == CommonStatus::Active { "ACTIVE" } else { "DISABLED" },
        "createdAt": s.created_at,
    })
}

fn account_json(u: &users::Model) -> Value {
    json!({
        "id": u.id, "employeeNo": u.employee_no, "realName": u.real_name, "email": u.email,
        "supplierId": u.supplier_id,
        "status": if u.status == CommonStatus::Active { "ACTIVE" } else { "DISABLED" },
        "lastLoginAt": u.last_login_at, "createdAt": u.created_at,
    })
}

fn validate_supplier(req: &SupplierUpsert) -> ApiResult<()> {
    if req.name.trim().is_empty() {
        return Err(AppError::BadRequest("供应商名称不能为空".into()));
    }
    if req.name.trim().chars().count() > 64 {
        return Err(AppError::BadRequest("供应商名称过长".into()));
    }
    let remark_len = req
        .remark
        .as_deref()
        .map(|s| s.chars().count())
        .unwrap_or(0);
    if remark_len > 500 {
        return Err(AppError::BadRequest("备注过长".into()));
    }
    Ok(())
}

async fn ensure_name_unique(
    db: &impl ConnectionTrait,
    name: &str,
    exclude_id: Option<u64>,
) -> ApiResult<()> {
    let mut query = suppliers::Entity::find().filter(suppliers::Column::Name.eq(name));
    if let Some(id) = exclude_id {
        query = query.filter(suppliers::Column::Id.ne(id));
    }
    if query.one(db).await?.is_some() {
        return Err(AppError::Conflict("供应商名称已存在".into()));
    }
    Ok(())
}

pub async fn list(db: &DatabaseConnection, q: &SupplierListQuery) -> ApiResult<PageResp<Value>> {
    let (page, size) = crate::dto::clamp_page(q.page, q.page_size);
    let mut cond = Condition::all();
    if let Some(kw) = q.keyword.as_ref().filter(|k| !k.trim().is_empty()) {
        cond = cond.add(Condition::any().add(suppliers::Column::Name.contains(kw.trim())));
    }
    if let Some(s) = &q.status {
        cond = cond.add(suppliers::Column::Status.eq(s.as_str()));
    }
    let paginator = suppliers::Entity::find()
        .filter(cond)
        .order_by_desc(suppliers::Column::Id)
        .paginate(db, size);
    let total = paginator.num_items().await?;
    let items = paginator.fetch_page(page - 1).await?;
    Ok(PageResp::new(
        items.iter().map(supplier_json).collect(),
        total,
        page,
        size,
    ))
}

pub async fn create(
    db: &DatabaseConnection,
    me: &CurrentUser,
    req: &SupplierUpsert,
) -> ApiResult<Value> {
    validate_supplier(req)?;
    let name = req.name.trim();
    let now = Utc::now();
    let txn = db.begin().await?;
    super::perm::lock_management_state(&txn).await?;
    super::perm::recheck_manager(&txn, me.id, "supplier:manage").await?;
    ensure_name_unique(&txn, name, None).await?;
    let model = suppliers::ActiveModel {
        name: Set(name.to_string()),
        remark: Set(req.remark.clone()),
        status: Set(CommonStatus::Active),
        created_by: Set(Some(me.id)),
        created_at: Set(now),
        updated_at: Set(now),
        ..Default::default()
    }
    .insert(&txn)
    .await
    .map_err(|error| crate::error::unique_conflict(error, "供应商名称已存在"))?;
    audit::insert(
        &txn,
        Some(me.id),
        Some(me.employee_no.clone()),
        "SUPPLIER_CREATE",
        Some("supplier"),
        Some(model.id.to_string()),
        Some(json!({
            "name": model.name,
            "status": "ACTIVE",
        })),
        None,
    )
    .await?;
    txn.commit().await?;
    Ok(supplier_json(&model))
}

pub async fn detail(db: &DatabaseConnection, id: u64) -> ApiResult<Value> {
    let s = suppliers::Entity::find_by_id(id)
        .one(db)
        .await?
        .ok_or(AppError::NotFound)?;
    Ok(supplier_json(&s))
}

pub async fn update(
    db: &DatabaseConnection,
    me: &CurrentUser,
    id: u64,
    req: &SupplierUpsert,
) -> ApiResult<Value> {
    validate_supplier(req)?;
    let txn = db.begin().await?;
    super::perm::lock_management_state(&txn).await?;
    super::perm::recheck_manager(&txn, me.id, "supplier:manage").await?;
    let s = suppliers::Entity::find_by_id(id)
        .one(&txn)
        .await?
        .ok_or(AppError::NotFound)?;
    let name = req.name.trim();
    let old_name = s.name.clone();
    ensure_name_unique(&txn, name, Some(id)).await?;
    let mut am: suppliers::ActiveModel = s.into();
    am.name = Set(name.to_string());
    am.remark = Set(req.remark.clone());
    am.updated_at = Set(Utc::now());
    let model = am
        .update(&txn)
        .await
        .map_err(|error| crate::error::unique_conflict(error, "供应商名称已存在"))?;
    audit::insert(
        &txn,
        Some(me.id),
        Some(me.employee_no.clone()),
        "SUPPLIER_UPDATE",
        Some("supplier"),
        Some(id.to_string()),
        Some(json!({
            "oldName": old_name,
            "newName": model.name,
            "changedFields": ["name", "remark"],
        })),
        None,
    )
    .await?;
    txn.commit().await?;
    Ok(supplier_json(&model))
}

pub async fn set_status(
    db: &DatabaseConnection,
    me: &CurrentUser,
    id: u64,
    status: &str,
) -> ApiResult<Value> {
    let st = match status {
        "ACTIVE" => CommonStatus::Active,
        "DISABLED" => CommonStatus::Disabled,
        _ => return Err(AppError::BadRequest("非法状态".into())),
    };
    let txn = db.begin().await?;
    super::perm::lock_management_state(&txn).await?;
    let s = suppliers::Entity::find_by_id(id)
        .lock_exclusive()
        .one(&txn)
        .await?
        .ok_or(AppError::NotFound)?;
    super::perm::recheck_manager(&txn, me.id, "supplier:manage").await?;
    let old_status = s.status;
    let name = s.name.clone();
    let mut am: suppliers::ActiveModel = s.into();
    am.status = Set(st);
    am.updated_at = Set(Utc::now());
    let model = am.update(&txn).await?;
    audit::insert(
        &txn,
        Some(me.id),
        Some(me.employee_no.clone()),
        "SUPPLIER_STATUS",
        Some("supplier"),
        Some(id.to_string()),
        Some(json!({
            "name": name,
            "oldStatus": if old_status == CommonStatus::Active { "ACTIVE" } else { "DISABLED" },
            "newStatus": status,
        })),
        None,
    )
    .await?;
    txn.commit().await?;
    Ok(supplier_json(&model))
}

// ---------- 供应商人员账号 ----------

async fn ensure_supplier_active(
    db: &impl ConnectionTrait,
    supplier_id: u64,
) -> ApiResult<suppliers::Model> {
    let s = suppliers::Entity::find_by_id(supplier_id)
        .one(db)
        .await?
        .ok_or(AppError::NotFound)?;
    if s.status != CommonStatus::Active {
        return Err(AppError::BadRequest("供应商已被禁用".into()));
    }
    Ok(s)
}

fn ensure_internal_account_manager(me: &CurrentUser) -> ApiResult<()> {
    if me.is_internal() {
        Ok(())
    } else {
        Err(AppError::Forbidden)
    }
}

pub async fn list_accounts(
    db: &DatabaseConnection,
    me: &CurrentUser,
    supplier_id: u64,
) -> ApiResult<Value> {
    ensure_internal_account_manager(me)?;
    super::perm::check_perm(db, me.id, "supplier:account").await?;
    suppliers::Entity::find_by_id(supplier_id)
        .one(db)
        .await?
        .ok_or(AppError::NotFound)?;
    let items = users::Entity::find()
        .filter(users::Column::UserType.eq(UserType::Supplier))
        .filter(users::Column::SupplierId.eq(supplier_id))
        .order_by_asc(users::Column::Id)
        .all(db)
        .await?;
    Ok(json!(items.iter().map(account_json).collect::<Vec<_>>()))
}

pub async fn create_account(
    db: &DatabaseConnection,
    me: &CurrentUser,
    supplier_id: u64,
    req: &AccountCreate,
) -> ApiResult<Value> {
    ensure_internal_account_manager(me)?;
    let employee_no = req.employee_no.trim();
    crate::util::validation::employee_no(employee_no)?;
    if req.real_name.trim().is_empty() || req.real_name.trim().chars().count() > 32 {
        return Err(AppError::BadRequest("姓名需为 1~32 个字符".into()));
    }
    if !password::strong_enough(&req.password) {
        return Err(AppError::BadRequest(password::POLICY_MESSAGE.into()));
    }
    crate::util::validation::email(&req.email)?;
    let now = Utc::now();
    // 账号与内置角色绑定同事务；事务内复核权限，避免权限撤销与写入竞态。
    let txn = db.begin().await?;
    super::perm::lock_management_state(&txn).await?;
    super::perm::recheck_manager(&txn, me.id, "supplier:account").await?;
    ensure_supplier_active(&txn, supplier_id).await?;
    if users::Entity::find()
        .filter(users::Column::EmployeeNo.eq(employee_no))
        .one(&txn)
        .await?
        .is_some()
    {
        return Err(AppError::Conflict("工号已存在".into()));
    }
    let model = users::ActiveModel {
        employee_no: Set(employee_no.to_string()),
        password_hash: Set(password::hash(&req.password)?),
        real_name: Set(req.real_name.trim().to_string()),
        email: Set(req.email.trim().to_string()),
        user_type: Set(UserType::Supplier),
        supplier_id: Set(Some(supplier_id)),
        department_id: Set(None),
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
    // 绑定内置供应商角色（固定权限集）
    let role = roles::Entity::find()
        .filter(roles::Column::IsBuiltIn.eq(true))
        .filter(roles::Column::Name.eq("供应商人员"))
        .one(&txn)
        .await?;
    let role = role.ok_or(AppError::Internal("内置供应商角色缺失".into()))?;
    user_roles::ActiveModel {
        user_id: Set(model.id),
        role_id: Set(role.id),
    }
    .insert(&txn)
    .await?;
    audit::insert(
        &txn,
        Some(me.id),
        Some(me.employee_no.clone()),
        "SUPPLIER_ACCOUNT_CREATE",
        Some("user"),
        Some(model.id.to_string()),
        Some(json!({
            "employeeNo": employee_no,
            "supplierId": supplier_id,
            "roleId": role.id,
            "roleName": role.name,
        })),
        None,
    )
    .await?;
    txn.commit().await?;
    Ok(account_json(&model))
}

async fn load_supplier_account(db: &impl ConnectionTrait, id: u64) -> ApiResult<users::Model> {
    let u = users::Entity::find_by_id(id)
        .one(db)
        .await?
        .ok_or(AppError::NotFound)?;
    if u.user_type != UserType::Supplier {
        return Err(AppError::BadRequest("该账号不是供应商人员".into()));
    }
    Ok(u)
}

pub async fn update_account(
    db: &DatabaseConnection,
    me: &CurrentUser,
    id: u64,
    req: &AccountUpdate,
) -> ApiResult<Value> {
    ensure_internal_account_manager(me)?;
    if let Some(v) = &req.real_name {
        if v.trim().is_empty() || v.trim().chars().count() > 32 {
            return Err(AppError::BadRequest("姓名需为 1~32 个字符".into()));
        }
    }
    if let Some(v) = &req.email {
        crate::util::validation::email(v)?;
    }
    let txn = db.begin().await?;
    super::perm::lock_management_state(&txn).await?;
    super::perm::recheck_manager(&txn, me.id, "supplier:account").await?;
    let u = load_supplier_account(&txn, id).await?;
    let employee_no = u.employee_no.clone();
    let mut am: users::ActiveModel = u.into();
    if let Some(v) = &req.real_name {
        am.real_name = Set(v.trim().to_string());
    }
    if let Some(v) = &req.email {
        am.email = Set(v.trim().to_string());
    }
    am.updated_at = Set(Utc::now());
    let model = am.update(&txn).await?;
    let mut changed_fields = Vec::new();
    if req.real_name.is_some() {
        changed_fields.push("realName");
    }
    if req.email.is_some() {
        changed_fields.push("email");
    }
    audit::insert(
        &txn,
        Some(me.id),
        Some(me.employee_no.clone()),
        "SUPPLIER_ACCOUNT_UPDATE",
        Some("user"),
        Some(id.to_string()),
        Some(json!({
            "employeeNo": employee_no,
            "changedFields": changed_fields,
        })),
        None,
    )
    .await?;
    txn.commit().await?;
    Ok(account_json(&model))
}

pub async fn set_account_status(
    db: &DatabaseConnection,
    me: &CurrentUser,
    id: u64,
    status: &str,
) -> ApiResult<Value> {
    ensure_internal_account_manager(me)?;
    let st = match status {
        "ACTIVE" => CommonStatus::Active,
        "DISABLED" => CommonStatus::Disabled,
        _ => return Err(AppError::BadRequest("非法状态".into())),
    };
    let txn = db.begin().await?;
    super::perm::lock_management_state(&txn).await?;
    let u = users::Entity::find_by_id(id)
        .lock_exclusive()
        .one(&txn)
        .await?
        .ok_or(AppError::NotFound)?;
    if u.user_type != UserType::Supplier {
        return Err(AppError::BadRequest("该账号不是供应商人员".into()));
    }
    super::perm::recheck_manager(&txn, me.id, "supplier:account").await?;
    let old_status = u.status;
    let employee_no = u.employee_no.clone();
    let mut am: users::ActiveModel = u.into();
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
        "SUPPLIER_ACCOUNT_STATUS",
        Some("user"),
        Some(id.to_string()),
        Some(json!({
            "employeeNo": employee_no,
            "oldStatus": if old_status == CommonStatus::Active { "ACTIVE" } else { "DISABLED" },
            "newStatus": status,
            "sessionsRevoked": st == CommonStatus::Disabled,
        })),
        None,
    )
    .await?;
    txn.commit().await?;
    Ok(account_json(&model))
}

pub async fn reset_account_password(
    db: &DatabaseConnection,
    me: &CurrentUser,
    id: u64,
    new_password: &str,
) -> ApiResult<()> {
    ensure_internal_account_manager(me)?;
    if !password::strong_enough(new_password) {
        return Err(AppError::BadRequest(password::POLICY_MESSAGE.into()));
    }
    let txn = db.begin().await?;
    super::perm::lock_management_state(&txn).await?;
    super::perm::recheck_manager(&txn, me.id, "supplier:account").await?;
    let u = load_supplier_account(&txn, id).await?;
    let employee_no = u.employee_no.clone();
    let mut am: users::ActiveModel = u.into();
    am.password_hash = Set(password::hash(new_password)?);
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
        "SUPPLIER_ACCOUNT_RESET_PASSWORD",
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

pub async fn delete(db: &DatabaseConnection, me: &CurrentUser, id: u64) -> ApiResult<()> {
    let txn = db.begin().await?;
    super::perm::lock_management_state(&txn).await?;
    super::perm::recheck_manager(&txn, me.id, "supplier:delete").await?;
    let supplier = suppliers::Entity::find_by_id(id)
        .one(&txn)
        .await?
        .ok_or(AppError::NotFound)?;
    let project_count = projects::Entity::find()
        .filter(projects::Column::SupplierId.eq(id))
        .count(&txn)
        .await?;
    if project_count > 0 {
        return Err(AppError::BadRequest(
            "该供应商仍有关联项目，请先删除项目".into(),
        ));
    }
    let account_count = users::Entity::find()
        .filter(users::Column::UserType.eq(UserType::Supplier))
        .filter(users::Column::SupplierId.eq(id))
        .count(&txn)
        .await?;
    if account_count > 0 {
        return Err(AppError::BadRequest(format!(
            "该供应商仍有 {account_count} 个账号，请先逐个处理账号"
        )));
    }
    audit::insert(
        &txn,
        Some(me.id),
        Some(me.employee_no.clone()),
        "SUPPLIER_DELETE",
        Some("supplier"),
        Some(id.to_string()),
        Some(json!({
            "name": supplier.name,
            "accountCount": 0,
        })),
        None,
    )
    .await?;
    suppliers::Entity::delete_by_id(id).exec(&txn).await?;
    txn.commit().await?;
    Ok(())
}

pub async fn delete_account(db: &DatabaseConnection, me: &CurrentUser, id: u64) -> ApiResult<()> {
    ensure_internal_account_manager(me)?;
    let txn = db.begin().await?;
    super::perm::lock_management_state(&txn).await?;
    let account = users::Entity::find_by_id(id)
        .lock_exclusive()
        .one(&txn)
        .await?
        .ok_or(AppError::NotFound)?;
    super::perm::recheck_manager(&txn, me.id, "supplier:account_delete").await?;
    if account.user_type != UserType::Supplier {
        return Err(AppError::BadRequest("该账号不是供应商人员".into()));
    }
    super::user::ensure_no_owned_content(&txn, id).await?;
    let employee_no = account.employee_no.clone();
    audit::insert(
        &txn,
        Some(me.id),
        Some(me.employee_no.clone()),
        "SUPPLIER_ACCOUNT_DELETE",
        Some("user"),
        Some(id.to_string()),
        Some(json!({ "employeeNo": employee_no })),
        None,
    )
    .await?;
    super::user::remove_login_bindings(&txn, id).await?;
    txn.commit().await?;
    Ok(())
}
