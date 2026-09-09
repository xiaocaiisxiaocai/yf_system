//! 组织架构：事业部 > 部门 > 课别
use chrono::Utc;
use sea_orm::{
    ActiveModelTrait, ColumnTrait, ConnectionTrait, DatabaseConnection, EntityTrait,
    PaginatorTrait, QueryFilter, Set, TransactionTrait,
};
use serde::Deserialize;
use serde_json::{json, Value};

use crate::entity::enums::{CommonStatus, DeptKind};
use crate::entity::{departments, users};
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
        return Err(AppError::BadRequest("组织名称需为 1~64 个字符".into()));
    }
    if req.sort_no.is_some_and(|value| value < 0) {
        return Err(AppError::BadRequest("排序号不能为负数".into()));
    }
    Ok(())
}

fn kind_for_parent(parent: Option<DeptKind>) -> ApiResult<DeptKind> {
    match parent {
        None => Ok(DeptKind::Division),
        Some(kind) => kind.child().ok_or_else(|| {
            AppError::BadRequest("课别下不能再新增下级，组织层级为 事业部 > 部门 > 课别".into())
        }),
    }
}

fn to_json(d: &departments::Model) -> Value {
    json!({
        "id": d.id, "name": d.name, "parentId": d.parent_id, "kind": d.kind.as_str(),
        "sortNo": d.sort_no, "status": status_str(&d.status),
    })
}

fn status_str(s: &CommonStatus) -> &'static str {
    match s {
        CommonStatus::Active => "ACTIVE",
        CommonStatus::Disabled => "DISABLED",
    }
}

/// 组织树：按 sort_no 排序，children 嵌套
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

async fn load_parent_kind(
    db: &impl ConnectionTrait,
    parent_id: Option<u64>,
) -> ApiResult<Option<DeptKind>> {
    match parent_id {
        None => Ok(None),
        Some(pid) => {
            let parent = departments::Entity::find_by_id(pid)
                .one(db)
                .await?
                .ok_or_else(|| AppError::BadRequest("上级组织不存在".into()))?;
            Ok(Some(parent.kind))
        }
    }
}

async fn max_depth_below(db: &impl ConnectionTrait, id: u64) -> ApiResult<u32> {
    let children = departments::Entity::find()
        .filter(departments::Column::ParentId.eq(id))
        .all(db)
        .await?;
    let mut depth = 0;
    for child in children {
        depth = depth.max(1 + Box::pin(max_depth_below(db, child.id)).await?);
    }
    Ok(depth)
}

fn allowed_depth_below(kind: DeptKind) -> u32 {
    match kind {
        DeptKind::Division => 2,
        DeptKind::Department => 1,
        DeptKind::Section => 0,
    }
}

async fn recompute_descendants(
    db: &impl ConnectionTrait,
    id: u64,
    kind: DeptKind,
) -> ApiResult<()> {
    let children = departments::Entity::find()
        .filter(departments::Column::ParentId.eq(id))
        .all(db)
        .await?;
    let child_kind = kind.child().ok_or_else(|| {
        AppError::BadRequest("课别下不能再有下级，超出 事业部 > 部门 > 课别".into())
    });
    if children.is_empty() {
        return Ok(());
    }
    let child_kind = child_kind?;
    for child in children {
        let child_id = child.id;
        let mut am: departments::ActiveModel = child.into();
        am.kind = Set(child_kind);
        am.update(db).await?;
        Box::pin(recompute_descendants(db, child_id, child_kind)).await?;
    }
    Ok(())
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
    let kind = kind_for_parent(load_parent_kind(&txn, req.parent_id).await?)?;
    let now = Utc::now();
    let model = departments::ActiveModel {
        name: Set(name.to_string()),
        parent_id: Set(req.parent_id),
        kind: Set(kind),
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
        Some(user.employee_no.clone()),
        "DEPT_CREATE",
        Some("department"),
        Some(model.id.to_string()),
        Some(json!({
            "name": model.name,
            "kind": model.kind.as_str(),
            "parentId": model.parent_id,
            "sortNo": model.sort_no,
        })),
        None,
    )
    .await?;
    txn.commit().await?;
    Ok(to_json(&model))
}

/// 父链检查：防止把节点挂到自己或自己的后代下
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
            "不能将组织移动到自身或其下级下".into(),
        ));
    }
    let kind = kind_for_parent(load_parent_kind(&txn, req.parent_id).await?)?;
    if max_depth_below(&txn, id).await? > allowed_depth_below(kind) {
        return Err(AppError::BadRequest(
            "超出 事业部 > 部门 > 课别 三级".into(),
        ));
    }
    let old_name = dept.name.clone();
    let old_parent_id = dept.parent_id;
    let mut am: departments::ActiveModel = dept.into();
    if !req.name.trim().is_empty() {
        am.name = Set(req.name.trim().to_string());
    }
    am.parent_id = Set(req.parent_id);
    am.kind = Set(kind);
    if let Some(s) = req.sort_no {
        am.sort_no = Set(s);
    }
    am.updated_at = Set(Utc::now());
    let model = am.update(&txn).await?;
    recompute_descendants(&txn, model.id, model.kind).await?;
    audit::insert(
        &txn,
        Some(user.id),
        Some(user.employee_no.clone()),
        "DEPT_UPDATE",
        Some("department"),
        Some(id.to_string()),
        Some(json!({
            "oldName": old_name,
            "newName": model.name,
            "kind": model.kind.as_str(),
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
        Some(user.employee_no.clone()),
        "DEPT_STATUS",
        Some("department"),
        Some(id.to_string()),
        Some(json!({
            "name": name,
            "kind": model.kind.as_str(),
            "oldStatus": if old_status == CommonStatus::Active { "ACTIVE" } else { "DISABLED" },
            "newStatus": status,
        })),
        None,
    )
    .await?;
    txn.commit().await?;
    Ok(to_json(&model))
}

pub async fn delete(db: &DatabaseConnection, user: &CurrentUser, id: u64) -> ApiResult<()> {
    let txn = db.begin().await?;
    super::perm::lock_management_state(&txn).await?;
    super::perm::recheck_manager(&txn, user.id, "dept:delete").await?;
    let dept = departments::Entity::find_by_id(id)
        .one(&txn)
        .await?
        .ok_or(AppError::NotFound)?;
    let child_count = departments::Entity::find()
        .filter(departments::Column::ParentId.eq(id))
        .count(&txn)
        .await?;
    if child_count > 0 {
        return Err(AppError::BadRequest("请先删除下级组织节点".into()));
    }
    let assigned = users::Entity::find()
        .filter(users::Column::DepartmentId.eq(id))
        .count(&txn)
        .await?;
    if assigned > 0 {
        return Err(AppError::BadRequest(
            "该组织仍有用户，请先调整用户归属或禁用组织".into(),
        ));
    }
    audit::insert(
        &txn,
        Some(user.id),
        Some(user.employee_no.clone()),
        "DEPT_DELETE",
        Some("department"),
        Some(id.to_string()),
        Some(json!({
            "name": dept.name,
            "kind": dept.kind.as_str(),
        })),
        None,
    )
    .await?;
    departments::Entity::delete_by_id(id).exec(&txn).await?;
    txn.commit().await?;
    Ok(())
}

/// 供用户模块使用：校验组织存在且启用
pub async fn ensure_active(db: &impl ConnectionTrait, id: u64) -> ApiResult<()> {
    let d = departments::Entity::find_by_id(id).one(db).await?;
    match d {
        Some(d) if d.status == CommonStatus::Active => Ok(()),
        Some(_) => Err(AppError::BadRequest("组织已被禁用".into())),
        None => Err(AppError::BadRequest("组织不存在".into())),
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

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn root_is_division() {
        assert_eq!(kind_for_parent(None).unwrap(), DeptKind::Division);
    }

    #[test]
    fn division_child_is_department() {
        assert_eq!(
            kind_for_parent(Some(DeptKind::Division)).unwrap(),
            DeptKind::Department
        );
    }

    #[test]
    fn department_child_is_section() {
        assert_eq!(
            kind_for_parent(Some(DeptKind::Department)).unwrap(),
            DeptKind::Section
        );
    }

    #[test]
    fn section_cannot_have_child() {
        assert!(kind_for_parent(Some(DeptKind::Section)).is_err());
    }
}
