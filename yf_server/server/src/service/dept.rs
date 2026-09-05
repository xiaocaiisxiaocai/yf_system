//! 组织架构（部门树）
use chrono::Utc;
use sea_orm::{
    ActiveModelTrait, ConnectionTrait, DatabaseConnection, EntityTrait, Set, TransactionTrait,
};
use serde::Deserialize;
use serde_json::{json, Value};

use crate::entity::departments;
use crate::entity::enums::CommonStatus;
use crate::error::{ApiResult, AppError};
use crate::middleware::auth::CurrentUser;

use super::audit;

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DeptUpsert {
    pub name: String,
    pub parent_id: Option<u64>,
    pub sort_no: Option<i32>,
}

fn validate(req: &DeptUpsert) -> ApiResult<()> {
    if req.name.trim().is_empty() || req.name.trim().chars().count() > 64 {
        return Err(AppError::BadRequest("部门名称需为 1~64 个字符".into()));
    }
    if req.sort_no.is_some_and(|value| value < 0) {
        return Err(AppError::BadRequest("排序号不能为负数".into()));
    }
    Ok(())
}

fn to_json(d: &departments::Model) -> Value {
    json!({
        "id": d.id, "name": d.name, "parentId": d.parent_id,
        "sortNo": d.sort_no, "status": status_str(&d.status),
    })
}

fn status_str(s: &CommonStatus) -> &'static str {
    match s {
        CommonStatus::Active => "ACTIVE",
        CommonStatus::Disabled => "DISABLED",
    }
}

/// 部门树：按 sort_no 排序，children 嵌套
pub async fn list(db: &DatabaseConnection) -> ApiResult<Value> {
    let mut all = departments::Entity::find().all(db).await?;
    all.sort_by_key(|d| d.sort_no);
    fn build(parent: Option<u64>, all: &[departments::Model]) -> Vec<Value> {
        all.iter()
            .filter(|d| d.parent_id == parent)
            .map(|d| {
                let mut v = to_json(d);
                v["children"] = json!(build(Some(d.id), all));
                v
            })
            .collect()
    }
    Ok(json!(build(None, &all)))
}

pub async fn create(
    db: &DatabaseConnection,
    user: &CurrentUser,
    req: &DeptUpsert,
) -> ApiResult<Value> {
    validate(req)?;
    let txn = db.begin().await?;
    super::perm::lock_management_state(&txn).await?;
    super::perm::recheck_manager(&txn, user.id, "dept:manage").await?;
    let name = req.name.trim();
    if let Some(pid) = req.parent_id {
        if departments::Entity::find_by_id(pid)
            .one(&txn)
            .await?
            .is_none()
        {
            return Err(AppError::BadRequest("父部门不存在".into()));
        }
    }
    let now = Utc::now();
    let model = departments::ActiveModel {
        name: Set(name.to_string()),
        parent_id: Set(req.parent_id),
        sort_no: Set(req.sort_no.unwrap_or(0)),
        status: Set(CommonStatus::Active),
        created_at: Set(now),
        updated_at: Set(now),
        ..Default::default()
    }
    .insert(&txn)
    .await?;
    audit::insert(
        &txn,
        Some(user.id),
        Some(user.username.clone()),
        "DEPT_CREATE",
        Some("department"),
        Some(model.id.to_string()),
        Some(json!({
            "name": model.name,
            "parentId": model.parent_id,
            "sortNo": model.sort_no,
        })),
        None,
    )
    .await?;
    txn.commit().await?;
    Ok(to_json(&model))
}

/// 父链检查：防止把部门挂到自己或自己的后代下
async fn would_cycle(
    db: &impl ConnectionTrait,
    id: u64,
    new_parent: Option<u64>,
) -> ApiResult<bool> {
    let mut cur = new_parent;
    let mut hops = 0;
    while let Some(pid) = cur {
        if pid == id {
            return Ok(true);
        }
        if hops > 32 {
            return Ok(true);
        }
        hops += 1;
        cur = departments::Entity::find_by_id(pid)
            .one(db)
            .await?
            .and_then(|d| d.parent_id);
    }
    Ok(false)
}

pub async fn update(
    db: &DatabaseConnection,
    user: &CurrentUser,
    id: u64,
    req: &DeptUpsert,
) -> ApiResult<Value> {
    validate(req)?;
    let txn = db.begin().await?;
    super::perm::lock_management_state(&txn).await?;
    super::perm::recheck_manager(&txn, user.id, "dept:manage").await?;
    let dept = departments::Entity::find_by_id(id)
        .one(&txn)
        .await?
        .ok_or(AppError::NotFound)?;
    if would_cycle(&txn, id, req.parent_id).await? {
        return Err(AppError::BadRequest(
            "不能将部门移动到自身或其子部门下".into(),
        ));
    }
    if let Some(pid) = req.parent_id {
        if departments::Entity::find_by_id(pid)
            .one(&txn)
            .await?
            .is_none()
        {
            return Err(AppError::BadRequest("父部门不存在".into()));
        }
    }
    let old_name = dept.name.clone();
    let old_parent_id = dept.parent_id;
    let mut am: departments::ActiveModel = dept.into();
    if !req.name.trim().is_empty() {
        am.name = Set(req.name.trim().to_string());
    }
    am.parent_id = Set(req.parent_id);
    if let Some(s) = req.sort_no {
        am.sort_no = Set(s);
    }
    am.updated_at = Set(Utc::now());
    let model = am.update(&txn).await?;
    audit::insert(
        &txn,
        Some(user.id),
        Some(user.username.clone()),
        "DEPT_UPDATE",
        Some("department"),
        Some(id.to_string()),
        Some(json!({
            "oldName": old_name,
            "newName": model.name,
            "oldParentId": old_parent_id,
            "newParentId": model.parent_id,
            "sortNo": model.sort_no,
        })),
        None,
    )
    .await?;
    txn.commit().await?;
    Ok(to_json(&model))
}

pub async fn set_status(
    db: &DatabaseConnection,
    user: &CurrentUser,
    id: u64,
    status: &str,
) -> ApiResult<Value> {
    let txn = db.begin().await?;
    super::perm::lock_management_state(&txn).await?;
    super::perm::recheck_manager(&txn, user.id, "dept:manage").await?;
    let dept = departments::Entity::find_by_id(id)
        .one(&txn)
        .await?
        .ok_or(AppError::NotFound)?;
    let st = match status {
        "ACTIVE" => CommonStatus::Active,
        "DISABLED" => CommonStatus::Disabled,
        _ => return Err(AppError::BadRequest("非法状态".into())),
    };
    let old_status = dept.status;
    let name = dept.name.clone();
    let mut am: departments::ActiveModel = dept.into();
    am.status = Set(st);
    am.updated_at = Set(Utc::now());
    let model = am.update(&txn).await?;
    audit::insert(
        &txn,
        Some(user.id),
        Some(user.username.clone()),
        "DEPT_STATUS",
        Some("department"),
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
    Ok(to_json(&model))
}

/// 供用户模块使用：校验部门存在且启用
pub async fn ensure_active(db: &impl ConnectionTrait, id: u64) -> ApiResult<()> {
    let d = departments::Entity::find_by_id(id).one(db).await?;
    match d {
        Some(d) if d.status == CommonStatus::Active => Ok(()),
        Some(_) => Err(AppError::BadRequest("部门已被禁用".into())),
        None => Err(AppError::BadRequest("部门不存在".into())),
    }
}

pub async fn name_of(db: &DatabaseConnection, id: Option<u64>) -> Option<String> {
    match id {
        Some(id) => departments::Entity::find_by_id(id)
            .one(db)
            .await
            .ok()
            .flatten()
            .map(|d| d.name),
        None => None,
    }
}
