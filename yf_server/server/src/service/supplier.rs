//! 供应商及其人员账号
use chrono::Utc;
use sea_orm::{
    ActiveModelTrait, ColumnTrait, Condition, DatabaseConnection, EntityTrait, PaginatorTrait,
    QueryFilter, QueryOrder, Set, TransactionTrait,
};
use serde::Deserialize;
use serde_json::{json, Value};

use crate::dto::PageResp;
use crate::entity::enums::{CommonStatus, UserType};
use crate::entity::{roles, suppliers, user_roles, users};
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
    pub contact_name: Option<String>,
    pub contact_phone: Option<String>,
    pub contact_email: Option<String>,
    pub address: Option<String>,
    pub remark: Option<String>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AccountCreate {
    pub employee_no: String,
    pub password: String,
    pub real_name: String,
    pub email: String,
    pub phone: Option<String>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AccountUpdate {
    pub real_name: Option<String>,
    pub email: Option<String>,
    pub phone: Option<String>,
}

fn supplier_json(s: &suppliers::Model) -> Value {
    json!({
        "id": s.id, "name": s.name,
        "contactName": s.contact_name, "contactPhone": s.contact_phone,
        "contactEmail": s.contact_email, "address": s.address, "remark": s.remark,
        "status": if s.status == CommonStatus::Active { "ACTIVE" } else { "DISABLED" },
        "createdAt": s.created_at,
    })
}

fn account_json(u: &users::Model) -> Value {
    json!({
        "id": u.id, "employeeNo": u.employee_no, "realName": u.real_name, "email": u.email,
        "phone": u.phone, "supplierId": u.supplier_id,
        "status": if u.status == CommonStatus::Active { "ACTIVE" } else { "DISABLED" },
        "lastLoginAt": u.last_login_at, "createdAt": u.created_at,
    })
}

fn validate_supplier(req: &SupplierUpsert) -> ApiResult<()> {
    if let Some(value) = req
        .contact_email
        .as_deref()
        .filter(|v| !v.trim().is_empty())
    {
        crate::util::validation::email(value)?;
    }
    if req.name.trim().is_empty() {
        return Err(AppError::BadRequest("供应商名称不能为空".into()));
    }
    if req.name.trim().chars().count() > 64 {
        return Err(AppError::BadRequest("供应商名称过长".into()));
    }
    let opt_len = |v: &Option<String>| v.as_deref().map(|s| s.chars().count()).unwrap_or(0);
    if opt_len(&req.contact_name) > 32
        || opt_len(&req.contact_phone) > 32
        || opt_len(&req.contact_email) > 128
    {
        return Err(AppError::BadRequest("联系方式字段过长".into()));
    }
    if opt_len(&req.address) > 256 || opt_len(&req.remark) > 500 {
        return Err(AppError::BadRequest("地址/备注过长".into()));
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
    let now = Utc::now();
    let txn = db.begin().await?;
    let model = suppliers::ActiveModel {
        name: Set(req.name.trim().to_string()),
        contact_name: Set(req.contact_name.clone()),
        contact_phone: Set(req.contact_phone.clone()),
        contact_email: Set(req.contact_email.clone()),
        address: Set(req.address.clone()),
        remark: Set(req.remark.clone()),
        status: Set(CommonStatus::Active),
        created_by: Set(Some(me.id)),
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
    let s = suppliers::Entity::find_by_id(id)
        .one(db)
        .await?
        .ok_or(AppError::NotFound)?;
    validate_supplier(req)?;
    let old_name = s.name.clone();
    let txn = db.begin().await?;
    let mut am: suppliers::ActiveModel = s.into();
    am.name = Set(req.name.trim().to_string());
    am.contact_name = Set(req.contact_name.clone());
    am.contact_phone = Set(req.contact_phone.clone());
    am.contact_email = Set(req.contact_email.clone());
    am.address = Set(req.address.clone());
    am.remark = Set(req.remark.clone());
    am.updated_at = Set(Utc::now());
    let model = am.update(&txn).await?;
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
            "changedFields": ["name", "contact", "address", "remark"],
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
    let s = suppliers::Entity::find_by_id(id)
        .one(db)
        .await?
        .ok_or(AppError::NotFound)?;
    let st = match status {
        "ACTIVE" => CommonStatus::Active,
        "DISABLED" => CommonStatus::Disabled,
        _ => return Err(AppError::BadRequest("非法状态".into())),
    };
    let old_status = s.status;
    let name = s.name.clone();
    let txn = db.begin().await?;
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
    db: &DatabaseConnection,
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

pub async fn list_accounts(db: &DatabaseConnection, supplier_id: u64) -> ApiResult<Value> {
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
    ensure_supplier_active(db, supplier_id).await?;
    let employee_no = req.employee_no.trim();
    crate::util::validation::employee_no(employee_no)?;
    crate::util::validation::phone(req.phone.as_deref())?;
    if req.real_name.trim().is_empty() || req.real_name.trim().chars().count() > 32 {
        return Err(AppError::BadRequest("姓名需为 1~32 个字符".into()));
    }
    if users::Entity::find()
        .filter(users::Column::EmployeeNo.eq(employee_no))
        .one(db)
        .await?
        .is_some()
    {
        return Err(AppError::BadRequest("工号已存在".into()));
    }
    if !password::strong_enough(&req.password) {
        return Err(AppError::BadRequest(
            "初始密码需至少 8 位、包含字母和数字，且不超过 128 字节".into(),
        ));
    }
    crate::util::validation::email(&req.email)?;
    let now = Utc::now();
    // 账号与内置角色绑定同事务
    let txn = db.begin().await?;
    let model = users::ActiveModel {
        employee_no: Set(employee_no.to_string()),
        password_hash: Set(password::hash(&req.password)?),
        real_name: Set(req.real_name.trim().to_string()),
        email: Set(req.email.trim().to_string()),
        phone: Set(req.phone.clone()),
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
    .await?;
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

async fn load_supplier_account(db: &DatabaseConnection, id: u64) -> ApiResult<users::Model> {
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
    let u = load_supplier_account(db, id).await?;
    if let Some(v) = &req.real_name {
        if v.trim().is_empty() || v.trim().chars().count() > 32 {
            return Err(AppError::BadRequest("姓名需为 1~32 个字符".into()));
        }
    }
    if let Some(v) = &req.email {
        crate::util::validation::email(v)?;
    }
    crate::util::validation::phone(req.phone.as_deref())?;
    let employee_no = u.employee_no.clone();
    let txn = db.begin().await?;
    let mut am: users::ActiveModel = u.into();
    if let Some(v) = &req.real_name {
        am.real_name = Set(v.trim().to_string());
    }
    if let Some(v) = &req.email {
        am.email = Set(v.trim().to_string());
    }
    if let Some(v) = &req.phone {
        am.phone = Set(Some(v.clone()));
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
    if req.phone.is_some() {
        changed_fields.push("phone");
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
    let u = load_supplier_account(db, id).await?;
    let st = match status {
        "ACTIVE" => CommonStatus::Active,
        "DISABLED" => CommonStatus::Disabled,
        _ => return Err(AppError::BadRequest("非法状态".into())),
    };
    let old_status = u.status;
    let employee_no = u.employee_no.clone();
    let txn = db.begin().await?;
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
    let u = load_supplier_account(db, id).await?;
    if !password::strong_enough(new_password) {
        return Err(AppError::BadRequest(
            "新密码需至少 8 位、包含字母和数字，且不超过 128 字节".into(),
        ));
    }
    let employee_no = u.employee_no.clone();
    let txn = db.begin().await?;
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
