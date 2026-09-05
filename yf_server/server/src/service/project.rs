//! 项目管理（关联单一供应商，数据范围见 scope）
use chrono::Utc;
use sea_orm::{
    ActiveModelTrait, ColumnTrait, Condition, DatabaseConnection, EntityTrait, PaginatorTrait,
    QueryFilter, QueryOrder, QuerySelect, Set, TransactionTrait,
};
use serde::Deserialize;
use serde_json::{json, Value};

use crate::dto::PageResp;
use crate::entity::enums::{CommonStatus, ProjectStatus, RoundStatus, UserType};
use crate::entity::{project_members, projects, suppliers, users};
use crate::error::{ApiResult, AppError};
use crate::middleware::auth::CurrentUser;

use super::{audit, scope};

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ProjectListQuery {
    #[serde(default = "crate::dto::default_page")]
    pub page: u64,
    #[serde(default = "crate::dto::default_page_size")]
    pub page_size: u64,
    pub keyword: Option<String>,
    pub status: Option<String>,
    pub supplier_id: Option<u64>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ProjectUpsert {
    pub code: String,
    pub name: String,
    pub description: Option<String>,
    pub supplier_id: u64,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct StatusChange {
    pub status: String,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct MembersSet {
    pub user_ids: Vec<u64>,
}

fn status_str(s: &ProjectStatus) -> &'static str {
    match s {
        ProjectStatus::Draft => "DRAFT",
        ProjectStatus::InProgress => "IN_PROGRESS",
        ProjectStatus::Completed => "COMPLETED",
        ProjectStatus::Terminated => "TERMINATED",
    }
}

fn transition_allowed(from: ProjectStatus, to: ProjectStatus) -> bool {
    matches!(
        (from, to),
        (ProjectStatus::Draft, ProjectStatus::InProgress)
            | (ProjectStatus::InProgress, ProjectStatus::Completed)
            | (ProjectStatus::InProgress, ProjectStatus::Terminated)
    )
}

pub async fn project_json(db: &DatabaseConnection, p: &projects::Model) -> Value {
    let supplier = suppliers::Entity::find_by_id(p.supplier_id)
        .one(db)
        .await
        .ok()
        .flatten();
    let creator = users::Entity::find_by_id(p.created_by)
        .one(db)
        .await
        .ok()
        .flatten();
    json!({
        "id": p.id, "code": p.code, "name": p.name, "description": p.description,
        "supplierId": p.supplier_id,
        "supplierName": supplier.as_ref().map(|s| s.name.clone()),
        "supplierCode": supplier.as_ref().map(|s| s.code.clone()),
        "status": status_str(&p.status),
        "createdBy": p.created_by,
        "createdByName": creator.map(|u| u.real_name),
        "createdAt": p.created_at, "updatedAt": p.updated_at,
    })
}

pub async fn list(
    db: &DatabaseConnection,
    user: &CurrentUser,
    q: &ProjectListQuery,
) -> ApiResult<PageResp<Value>> {
    let (page, size) = crate::dto::clamp_page(q.page, q.page_size);
    let mut cond = scope::project_condition(db, user).await?;
    if let Some(kw) = q.keyword.as_ref().filter(|k| !k.trim().is_empty()) {
        cond = cond.add(
            Condition::any()
                .add(projects::Column::Name.contains(kw.trim()))
                .add(projects::Column::Code.contains(kw.trim())),
        );
    }
    if let Some(s) = &q.status {
        cond = cond.add(projects::Column::Status.eq(s.as_str()));
    }
    if let Some(sid) = q.supplier_id {
        cond = cond.add(projects::Column::SupplierId.eq(sid));
    }
    let paginator = projects::Entity::find()
        .filter(cond)
        .order_by_desc(projects::Column::Id)
        .paginate(db, size);
    let total = paginator.num_items().await?;
    let items = paginator.fetch_page(page - 1).await?;
    // 批量预取供应商/创建人，避免每行 2 次查询的 N+1
    let supplier_ids: Vec<u64> = items.iter().map(|p| p.supplier_id).collect();
    let creator_ids: Vec<u64> = items.iter().map(|p| p.created_by).collect();
    let supplier_map: std::collections::HashMap<u64, (String, String)> = suppliers::Entity::find()
        .filter(suppliers::Column::Id.is_in(supplier_ids))
        .all(db)
        .await?
        .into_iter()
        .map(|s| (s.id, (s.name, s.code)))
        .collect();
    let creator_map: std::collections::HashMap<u64, String> = users::Entity::find()
        .filter(users::Column::Id.is_in(creator_ids))
        .all(db)
        .await?
        .into_iter()
        .map(|u| (u.id, u.real_name))
        .collect();
    let mut list = Vec::with_capacity(items.len());
    for p in &items {
        let sup = supplier_map.get(&p.supplier_id);
        list.push(json!({
            "id": p.id, "code": p.code, "name": p.name, "description": p.description,
            "supplierId": p.supplier_id,
            "supplierName": sup.map(|t| &t.0),
            "supplierCode": sup.map(|t| &t.1),
            "status": status_str(&p.status),
            "createdBy": p.created_by,
            "createdByName": creator_map.get(&p.created_by),
            "createdAt": p.created_at, "updatedAt": p.updated_at,
        }));
    }
    Ok(PageResp::new(list, total, page, size))
}

fn validate_description(description: Option<&str>) -> ApiResult<()> {
    if description.map(|d| d.chars().count()).unwrap_or(0) > 500 {
        return Err(AppError::BadRequest("项目说明过长（最多 500 字）".into()));
    }
    Ok(())
}

pub async fn create(
    db: &DatabaseConnection,
    me: &CurrentUser,
    req: &ProjectUpsert,
) -> ApiResult<Value> {
    if !me.is_internal() {
        return Err(AppError::Forbidden);
    }
    let code = req.code.trim();
    if req.name.trim().is_empty() || code.is_empty() {
        return Err(AppError::BadRequest("项目编号和名称不能为空".into()));
    }
    if code.chars().count() > 64 || req.name.trim().chars().count() > 128 {
        return Err(AppError::BadRequest("项目编号/名称过长".into()));
    }
    validate_description(req.description.as_deref())?;
    if projects::Entity::find()
        .filter(projects::Column::Code.eq(code))
        .one(db)
        .await?
        .is_some()
    {
        return Err(AppError::BadRequest("项目编号已存在".into()));
    }
    let supplier = suppliers::Entity::find_by_id(req.supplier_id)
        .one(db)
        .await?
        .ok_or(AppError::BadRequest("供应商不存在".into()))?;
    if supplier.status != CommonStatus::Active {
        return Err(AppError::BadRequest("供应商已被禁用".into()));
    }
    let now = Utc::now();
    // 项目与创建人成员记录同事务，避免半成功状态
    let txn = db.begin().await?;
    let model = projects::ActiveModel {
        code: Set(code.to_string()),
        name: Set(req.name.trim().to_string()),
        description: Set(req.description.clone()),
        supplier_id: Set(req.supplier_id),
        status: Set(ProjectStatus::Draft),
        created_by: Set(me.id),
        created_at: Set(now),
        updated_at: Set(now),
        ..Default::default()
    }
    .insert(&txn)
    .await?;
    // 创建人自动加入项目成员，保证数据范围一致
    project_members::ActiveModel {
        project_id: Set(model.id),
        user_id: Set(me.id),
        created_by: Set(Some(me.id)),
        created_at: Set(now),
    }
    .insert(&txn)
    .await?;
    audit::insert(
        &txn,
        Some(me.id),
        Some(me.username.clone()),
        "PROJECT_CREATE",
        Some("project"),
        Some(model.id.to_string()),
        Some(json!({"name": model.name})),
        None,
    )
    .await?;
    txn.commit().await?;
    Ok(project_json(db, &model).await)
}

pub async fn detail(db: &DatabaseConnection, user: &CurrentUser, id: u64) -> ApiResult<Value> {
    let p = scope::ensure_project_access(db, user, id).await?;
    let mut v = project_json(db, &p).await;
    let members = list_members(db, id).await?;
    v["members"] = members;
    Ok(v)
}

pub async fn update(
    db: &DatabaseConnection,
    me: &CurrentUser,
    id: u64,
    req: &ProjectUpsert,
) -> ApiResult<Value> {
    scope::ensure_project_access(db, me, id).await?;
    let code = req.code.trim();
    let name = req.name.trim();
    if code.is_empty() || code.chars().count() > 64 {
        return Err(AppError::BadRequest("项目编码需为 1~64 个字符".into()));
    }
    if name.is_empty() || name.chars().count() > 128 {
        return Err(AppError::BadRequest("项目名称需为 1~128 个字符".into()));
    }
    validate_description(req.description.as_deref())?;
    if let Some(other) = projects::Entity::find()
        .filter(projects::Column::Code.eq(code))
        .one(db)
        .await?
    {
        if other.id != id {
            return Err(AppError::BadRequest("项目编号已存在".into()));
        }
    }
    let p = projects::Entity::find_by_id(id)
        .one(db)
        .await?
        .ok_or(AppError::NotFound)?;
    if p.supplier_id != req.supplier_id {
        return Err(AppError::BadRequest(
            "项目创建后不可更换供应商；请新建项目以避免历史数据越权".into(),
        ));
    }
    let txn = db.begin().await?;
    let mut am: projects::ActiveModel = p.into();
    am.code = Set(code.to_string());
    am.name = Set(name.to_string());
    am.description = Set(req.description.clone());
    am.supplier_id = Set(req.supplier_id);
    am.updated_at = Set(Utc::now());
    let model = am.update(&txn).await?;
    audit::insert(
        &txn,
        Some(me.id),
        Some(me.username.clone()),
        "PROJECT_UPDATE",
        Some("project"),
        Some(id.to_string()),
        None,
        None,
    )
    .await?;
    txn.commit().await?;
    Ok(project_json(db, &model).await)
}

/// 状态流转：DRAFT→IN_PROGRESS→COMPLETED/TERMINATED；终态只读。
pub async fn set_status(
    db: &DatabaseConnection,
    me: &CurrentUser,
    id: u64,
    req: &StatusChange,
) -> ApiResult<Value> {
    let p = scope::ensure_project_access(db, me, id).await?;
    let from = p.status;
    let to = match req.status.as_str() {
        "DRAFT" => ProjectStatus::Draft,
        "IN_PROGRESS" => ProjectStatus::InProgress,
        "COMPLETED" => ProjectStatus::Completed,
        "TERMINATED" => ProjectStatus::Terminated,
        _ => return Err(AppError::BadRequest("非法项目状态".into())),
    };
    let allowed = transition_allowed(from, to);
    if !allowed {
        return Err(AppError::Conflict(format!(
            "项目状态不允许从 {} 变更为 {}",
            status_str(&from),
            status_str(&to)
        )));
    }
    let mut am: projects::ActiveModel = p.into();
    am.status = Set(to);
    am.updated_at = Set(Utc::now());
    // CAS：仅当状态仍为原值时流转，防并发重复变更
    let txn = db.begin().await?;
    let upd = projects::Entity::update_many()
        .set(am)
        .filter(projects::Column::Id.eq(id))
        .filter(projects::Column::Status.eq(from))
        .exec(&txn)
        .await?;
    if upd.rows_affected == 0 {
        txn.rollback().await?;
        return Err(AppError::Conflict(
            "项目状态已被他人变更，请刷新后重试".into(),
        ));
    }
    audit::insert(
        &txn,
        Some(me.id),
        Some(me.username.clone()),
        "PROJECT_STATUS",
        Some("project"),
        Some(id.to_string()),
        Some(json!({"to": req.status})),
        None,
    )
    .await?;
    txn.commit().await?;
    let model = projects::Entity::find_by_id(id)
        .one(db)
        .await?
        .ok_or(AppError::NotFound)?;
    Ok(project_json(db, &model).await)
}

pub async fn list_members(db: &DatabaseConnection, project_id: u64) -> ApiResult<Value> {
    let rows = project_members::Entity::find()
        .filter(project_members::Column::ProjectId.eq(project_id))
        .all(db)
        .await?;
    // 批量取用户，避免逐成员查询
    let user_map: std::collections::HashMap<u64, users::Model> = users::Entity::find()
        .filter(users::Column::Id.is_in(rows.iter().map(|m| m.user_id).collect::<Vec<_>>()))
        .all(db)
        .await?
        .into_iter()
        .map(|u| (u.id, u))
        .collect();
    let mut list = Vec::with_capacity(rows.len());
    for m in rows {
        if let Some(u) = user_map.get(&m.user_id) {
            list.push(json!({
                "userId": u.id, "username": u.username, "realName": u.real_name,
                "departmentId": u.department_id,
                "status": if u.status == CommonStatus::Active { "ACTIVE" } else { "DISABLED" },
                "createdAt": m.created_at,
            }));
        }
    }
    Ok(json!(list))
}

pub async fn set_members(
    db: &DatabaseConnection,
    me: &CurrentUser,
    project_id: u64,
    req: &MembersSet,
) -> ApiResult<()> {
    scope::ensure_project_access(db, me, project_id).await?;
    if req.user_ids.len() > 200 {
        return Err(AppError::BadRequest("成员数量超过上限".into()));
    }
    let mut ids = req.user_ids.clone();
    if !ids.contains(&me.id) {
        ids.push(me.id); // 操作者必须保留在项目内，防止把自己移出后失去管理入口
    }
    ids.sort_unstable();
    ids.dedup();
    // 删除+重建必须在同一事务，任一插入失败则整体回滚
    let txn = db.begin().await?;
    // 与账号/角色变更共用管理锁；必须先管理锁再项目锁，且在快照读取前取得。
    super::perm::lock_management_state(&txn).await?;
    // 和上传最终授权检查串行化，成员移除提交后旧会话不能继续写入。
    projects::Entity::find_by_id(project_id)
        .lock_exclusive()
        .one(&txn)
        .await?
        .ok_or(AppError::NotFound)?;
    super::perm::recheck_manager(&txn, me.id, "project:member").await?;
    scope::ensure_project_access(&txn, me, project_id).await?;
    // 等待期间目标或操作者可能已停用；校验最终完整集合（包括保留的操作者）。
    for uid in &ids {
        let u = users::Entity::find_by_id(*uid)
            .one(&txn)
            .await?
            .ok_or(AppError::BadRequest(format!("用户不存在: {uid}")))?;
        if u.user_type != UserType::Internal || u.status != CommonStatus::Active {
            return Err(AppError::BadRequest(format!(
                "用户 {} 不是启用的内部账号",
                u.username
            )));
        }
    }
    project_members::Entity::delete_many()
        .filter(project_members::Column::ProjectId.eq(project_id))
        .exec(&txn)
        .await?;
    let now = Utc::now();
    for uid in ids {
        project_members::ActiveModel {
            project_id: Set(project_id),
            user_id: Set(uid),
            created_by: Set(Some(me.id)),
            created_at: Set(now),
        }
        .insert(&txn)
        .await?;
    }
    audit::insert(
        &txn,
        Some(me.id),
        Some(me.username.clone()),
        "PROJECT_MEMBERS",
        Some("project"),
        Some(project_id.to_string()),
        None,
        None,
    )
    .await?;
    txn.commit().await?;
    Ok(())
}

/// 工作台/列表徽标：未读留言数 + 待我方确认轮次数
pub async fn summary(
    db: &DatabaseConnection,
    user: &CurrentUser,
    project_id: u64,
) -> ApiResult<Value> {
    scope::ensure_project_access(db, user, project_id).await?;
    let unread = crate::service::message::unread_count(db, user.id, project_id).await?;
    let my_side = if user.user_type == UserType::Supplier {
        crate::entity::enums::ConfirmSide::Supplier
    } else {
        crate::entity::enums::ConfirmSide::Company
    };
    let pending_rounds = crate::entity::rounds::Entity::find()
        .filter(crate::entity::rounds::Column::ProjectId.eq(project_id))
        .filter(crate::entity::rounds::Column::Status.eq(RoundStatus::Pending))
        .filter(crate::entity::rounds::Column::ConfirmSide.eq(my_side))
        .count(db)
        .await?;
    Ok(json!({ "unreadMessages": unread, "pendingRounds": pending_rounds }))
}

/// 下拉选项：启用中的供应商（仅内部用户可用，用于新建项目）
pub async fn supplier_options(db: &DatabaseConnection, user: &CurrentUser) -> ApiResult<Value> {
    if !user.is_internal() {
        return Err(AppError::Forbidden);
    }
    let rows = suppliers::Entity::find()
        .filter(suppliers::Column::Status.eq(CommonStatus::Active))
        .all(db)
        .await?;
    Ok(json!(rows
        .iter()
        .map(|s| json!({ "id": s.id, "name": s.name, "code": s.code }))
        .collect::<Vec<_>>()))
}

/// 下拉选项：启用中的内部用户（仅内部用户可用，用于项目成员设置）
pub async fn internal_user_options(
    db: &DatabaseConnection,
    user: &CurrentUser,
) -> ApiResult<Value> {
    if !user.is_internal() {
        return Err(AppError::Forbidden);
    }
    let rows = users::Entity::find()
        .filter(users::Column::UserType.eq(UserType::Internal))
        .filter(users::Column::Status.eq(CommonStatus::Active))
        .all(db)
        .await?;
    // 批量取部门名，避免逐用户查询
    let dept_map: std::collections::HashMap<u64, String> =
        crate::entity::departments::Entity::find()
            .filter(
                crate::entity::departments::Column::Id.is_in(
                    rows.iter()
                        .filter_map(|u| u.department_id)
                        .collect::<Vec<_>>(),
                ),
            )
            .all(db)
            .await?
            .into_iter()
            .map(|d| (d.id, d.name))
            .collect();
    let mut out = Vec::with_capacity(rows.len());
    for u in &rows {
        out.push(json!({
            "id": u.id, "username": u.username, "realName": u.real_name,
            "deptName": u.department_id.and_then(|d| dept_map.get(&d)),
        }));
    }
    Ok(json!(out))
}

#[cfg(test)]
mod tests {
    use super::transition_allowed;
    use crate::entity::enums::ProjectStatus;

    #[test]
    fn completed_and_terminated_projects_are_terminal() {
        assert!(!transition_allowed(
            ProjectStatus::Completed,
            ProjectStatus::InProgress
        ));
        assert!(!transition_allowed(
            ProjectStatus::Terminated,
            ProjectStatus::InProgress
        ));
        assert!(transition_allowed(
            ProjectStatus::Draft,
            ProjectStatus::InProgress
        ));
    }
}
