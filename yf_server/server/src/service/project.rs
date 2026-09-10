//! 项目管理（关联单一供应商，数据范围见 scope）
use chrono::Utc;
use sea_orm::{
    ActiveModelTrait, ColumnTrait, ConnectionTrait, DatabaseConnection, EntityTrait,
    PaginatorTrait, QueryFilter, QueryOrder, QuerySelect, Set, TransactionTrait,
};
use serde::Deserialize;
use serde_json::{json, Value};

use crate::dto::PageResp;
use crate::entity::enums::{CommonStatus, ConfirmSide, ProjectStatus, UserType};
use crate::entity::{
    departments, email_outbox, files, messages, project_activities, project_members,
    project_status_logs, projects, suppliers, upload_sessions, users,
};
use crate::error::{ApiResult, AppError};
use crate::middleware::auth::CurrentUser;
use crate::state::AppState;

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
pub struct SubmitReq {
    pub confirm_side: String,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RejectReq {
    pub reason: String,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct MembersSet {
    pub user_ids: Vec<u64>,
}

pub fn status_str(s: &ProjectStatus) -> &'static str {
    match s {
        ProjectStatus::Draft => "DRAFT",
        ProjectStatus::InProgress => "IN_PROGRESS",
        ProjectStatus::PendingConfirmation => "PENDING_CONFIRMATION",
        ProjectStatus::Completed => "COMPLETED",
        ProjectStatus::Terminated => "TERMINATED",
    }
}

fn confirm_side_str(side: ConfirmSide) -> &'static str {
    match side {
        ConfirmSide::Company => "COMPANY",
        ConfirmSide::Supplier => "SUPPLIER",
    }
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
        "id": p.id, "name": p.name, "description": p.description,
        "supplierId": p.supplier_id,
        "supplierName": supplier.as_ref().map(|s| s.name.clone()),
        "status": status_str(&p.status),
        "confirmSide": p.confirm_side.map(confirm_side_str),
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
        cond = cond.add(projects::Column::Name.contains(kw.trim()));
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
    let supplier_map: std::collections::HashMap<u64, String> = suppliers::Entity::find()
        .filter(suppliers::Column::Id.is_in(supplier_ids))
        .all(db)
        .await?
        .into_iter()
        .map(|s| (s.id, s.name))
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
            "id": p.id, "name": p.name, "description": p.description,
            "supplierId": p.supplier_id,
            "supplierName": sup,
            "status": status_str(&p.status),
            "confirmSide": p.confirm_side.map(confirm_side_str),
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

async fn ensure_name_unique(
    db: &impl ConnectionTrait,
    name: &str,
    exclude_id: Option<u64>,
) -> ApiResult<()> {
    let mut query = projects::Entity::find().filter(projects::Column::Name.eq(name));
    if let Some(id) = exclude_id {
        query = query.filter(projects::Column::Id.ne(id));
    }
    if query.one(db).await?.is_some() {
        return Err(AppError::Conflict("项目名称已存在".into()));
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
    let name = req.name.trim();
    if name.is_empty() {
        return Err(AppError::BadRequest("项目名称不能为空".into()));
    }
    if name.chars().count() > 128 {
        return Err(AppError::BadRequest("项目名称过长".into()));
    }
    validate_description(req.description.as_deref())?;
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
    ensure_name_unique(&txn, name, None).await?;
    let model = projects::ActiveModel {
        name: Set(name.to_string()),
        description: Set(req.description.clone()),
        supplier_id: Set(req.supplier_id),
        status: Set(ProjectStatus::Draft),
        confirm_side: Set(None),
        created_by: Set(me.id),
        created_at: Set(now),
        updated_at: Set(now),
        ..Default::default()
    }
    .insert(&txn)
    .await
    .map_err(|error| crate::error::unique_conflict(error, "项目名称已存在"))?;
    // 创建人自动加入项目成员，保证数据范围一致
    project_members::ActiveModel {
        project_id: Set(model.id),
        user_id: Set(me.id),
        created_by: Set(Some(me.id)),
        created_at: Set(now),
    }
    .insert(&txn)
    .await?;
    project_status_logs::ActiveModel {
        project_id: Set(model.id),
        from_status: Set(None),
        to_status: Set(ProjectStatus::Draft),
        action: Set("CREATE".to_owned()),
        operator_id: Set(me.id),
        confirm_side: Set(None),
        reason: Set(None),
        created_at: Set(now),
        ..Default::default()
    }
    .insert(&txn)
    .await?;
    audit::insert(
        &txn,
        Some(me.id),
        Some(me.employee_no.clone()),
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
    let latest_reject = project_status_logs::Entity::find()
        .filter(project_status_logs::Column::ProjectId.eq(id))
        .filter(project_status_logs::Column::Action.eq("REJECT"))
        .order_by_desc(project_status_logs::Column::Id)
        .one(db)
        .await?;
    let latest_submit = project_status_logs::Entity::find()
        .filter(project_status_logs::Column::ProjectId.eq(id))
        .filter(project_status_logs::Column::Action.eq("SUBMIT"))
        .order_by_desc(project_status_logs::Column::Id)
        .one(db)
        .await?;
    v["rejectReason"] = json!(latest_reject.and_then(|row| row.reason));
    v["latestSubmitterId"] = json!(latest_submit.map(|row| row.operator_id));
    Ok(v)
}

pub async fn update(
    db: &DatabaseConnection,
    me: &CurrentUser,
    id: u64,
    req: &ProjectUpsert,
) -> ApiResult<Value> {
    let name = req.name.trim();
    if name.is_empty() || name.chars().count() > 128 {
        return Err(AppError::BadRequest("项目名称需为 1~128 个字符".into()));
    }
    validate_description(req.description.as_deref())?;
    let txn = db.begin().await?;
    super::perm::lock_management_state(&txn).await?;
    let p = projects::Entity::find_by_id(id)
        .lock_exclusive()
        .one(&txn)
        .await?
        .ok_or(AppError::NotFound)?;
    super::perm::recheck_manager(&txn, me.id, "project:update").await?;
    scope::ensure_project_access(&txn, me, id).await?;
    if !matches!(p.status, ProjectStatus::Draft | ProjectStatus::InProgress) {
        return Err(AppError::Conflict("项目当前状态不可编辑".into()));
    }
    if p.supplier_id != req.supplier_id {
        return Err(AppError::BadRequest(
            "项目创建后不可更换供应商；请新建项目以避免历史数据越权".into(),
        ));
    }
    ensure_name_unique(&txn, name, Some(id)).await?;
    let mut am: projects::ActiveModel = p.into();
    am.name = Set(name.to_string());
    am.description = Set(req.description.clone());
    am.supplier_id = Set(req.supplier_id);
    am.updated_at = Set(Utc::now());
    let model = am
        .update(&txn)
        .await
        .map_err(|error| crate::error::unique_conflict(error, "项目名称已存在"))?;
    audit::insert(
        &txn,
        Some(me.id),
        Some(me.employee_no.clone()),
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

struct Transition<'a> {
    to: ProjectStatus,
    action: &'a str,
    next_confirm_side: Option<ConfirmSide>,
    history_confirm_side: Option<ConfirmSide>,
    reason: Option<String>,
}

fn workflow_audit_action(action: &str) -> ApiResult<&'static str> {
    match action {
        "START" => Ok("PROJECT_START"),
        "SUBMIT" => Ok("PROJECT_SUBMIT"),
        "CONFIRM" => Ok("PROJECT_CONFIRM"),
        "REJECT" => Ok("PROJECT_REJECT"),
        "WITHDRAW" => Ok("PROJECT_WITHDRAW"),
        "TERMINATE" => Ok("PROJECT_TERMINATE"),
        "RESTART" => Ok("PROJECT_RESTART"),
        _ => Err(AppError::Internal(format!("未知项目流程动作: {action}"))),
    }
}

async fn apply_transition(
    txn: &sea_orm::DatabaseTransaction,
    me: &CurrentUser,
    project: projects::Model,
    transition: Transition<'_>,
) -> ApiResult<()> {
    let Transition {
        to,
        action,
        next_confirm_side,
        history_confirm_side,
        reason,
    } = transition;
    let from = project.status;
    let project_id = project.id;
    let audit_action = workflow_audit_action(action)?;
    let now = Utc::now();
    let mut update: projects::ActiveModel = project.into();
    update.status = Set(to);
    update.confirm_side = Set(next_confirm_side);
    update.updated_at = Set(now);
    let result = projects::Entity::update_many()
        .set(update)
        .filter(projects::Column::Id.eq(project_id))
        .filter(projects::Column::Status.eq(from))
        .exec(txn)
        .await?;
    if result.rows_affected == 0 {
        return Err(AppError::Conflict(
            "项目状态已被他人变更，请刷新后重试".into(),
        ));
    }
    let status_log = project_status_logs::ActiveModel {
        project_id: Set(project_id),
        from_status: Set(Some(from)),
        to_status: Set(to),
        action: Set(action.to_owned()),
        operator_id: Set(me.id),
        confirm_side: Set(history_confirm_side),
        reason: Set(reason.clone()),
        created_at: Set(now),
        ..Default::default()
    }
    .insert(txn)
    .await?;
    audit::insert(
        txn,
        Some(me.id),
        Some(me.employee_no.clone()),
        audit_action,
        Some("project"),
        Some(project_id.to_string()),
        Some(json!({
            "from": status_str(&from),
            "to": status_str(&to),
            "action": action,
            "confirmSide": history_confirm_side.map(confirm_side_str),
            "reason": reason,
            "statusLogId": status_log.id,
        })),
        None,
    )
    .await?;
    Ok(())
}

/// 管理状态动作：开始、终止、重新开始。完成只能由验收确认产生。
pub async fn set_status(
    db: &DatabaseConnection,
    me: &CurrentUser,
    id: u64,
    req: &StatusChange,
) -> ApiResult<Value> {
    if !me.is_internal() {
        return Err(AppError::Forbidden);
    }
    let txn = db.begin().await?;
    super::perm::lock_management_state(&txn).await?;
    let p = projects::Entity::find_by_id(id)
        .lock_exclusive()
        .one(&txn)
        .await?
        .ok_or(AppError::NotFound)?;
    super::perm::recheck_manager(&txn, me.id, "project:status").await?;
    scope::ensure_project_access(&txn, me, id).await?;
    let (to, action) = match (p.status, req.status.as_str()) {
        (ProjectStatus::Draft, "IN_PROGRESS") => (ProjectStatus::InProgress, "START"),
        (ProjectStatus::Terminated, "IN_PROGRESS") => (ProjectStatus::InProgress, "RESTART"),
        (ProjectStatus::InProgress, "TERMINATED") => (ProjectStatus::Terminated, "TERMINATE"),
        (_, "IN_PROGRESS" | "TERMINATED") => {
            return Err(AppError::Conflict(format!(
                "项目当前状态 {} 不允许执行该管理动作",
                status_str(&p.status)
            )))
        }
        _ => {
            return Err(AppError::BadRequest(
                "仅允许开始、终止或重新开始项目".into(),
            ))
        }
    };
    if action == "TERMINATE" {
        let active_uploads = upload_sessions::Entity::find()
            .filter(upload_sessions::Column::ProjectId.eq(id))
            .filter(upload_sessions::Column::Status.is_in([
                crate::entity::enums::UploadStatus::Uploading,
                crate::entity::enums::UploadStatus::Merging,
            ]))
            .count(&txn)
            .await?;
        if active_uploads > 0 {
            return Err(AppError::Conflict("项目仍有活动上传会话，不能终止".into()));
        }
    }
    apply_transition(
        &txn,
        me,
        p,
        Transition {
            to,
            action,
            next_confirm_side: None,
            history_confirm_side: None,
            reason: None,
        },
    )
    .await?;
    txn.commit().await?;
    let model = projects::Entity::find_by_id(id)
        .one(db)
        .await?
        .ok_or(AppError::NotFound)?;
    Ok(project_json(db, &model).await)
}

fn user_side(user: &CurrentUser) -> ConfirmSide {
    if user.user_type == UserType::Supplier {
        ConfirmSide::Supplier
    } else {
        ConfirmSide::Company
    }
}

fn parse_confirm_side(value: &str) -> ApiResult<ConfirmSide> {
    match value {
        "COMPANY" => Ok(ConfirmSide::Company),
        "SUPPLIER" => Ok(ConfirmSide::Supplier),
        _ => Err(AppError::BadRequest(
            "确认方必须为 COMPANY 或 SUPPLIER".into(),
        )),
    }
}

async fn lock_workflow_project(
    txn: &sea_orm::DatabaseTransaction,
    me: &CurrentUser,
    project_id: u64,
    permission: &str,
) -> ApiResult<projects::Model> {
    super::perm::lock_business_state(txn).await?;
    let project = projects::Entity::find_by_id(project_id)
        .lock_exclusive()
        .one(txn)
        .await?
        .ok_or(AppError::NotFound)?;
    super::perm::recheck_manager(txn, me.id, permission).await?;
    scope::ensure_project_access(txn, me, project_id).await?;
    Ok(project)
}

pub async fn submit(
    db: &DatabaseConnection,
    base_url: &str,
    me: &CurrentUser,
    project_id: u64,
    req: &SubmitReq,
) -> ApiResult<Value> {
    let side = parse_confirm_side(req.confirm_side.trim())?;
    if side == user_side(me) {
        return Err(AppError::BadRequest("确认方必须选择提交人的另一方".into()));
    }
    let txn = db.begin().await?;
    let project = lock_workflow_project(&txn, me, project_id, "project:submit").await?;
    if project.status != ProjectStatus::InProgress {
        return Err(AppError::Conflict("只有进行中的项目可以提交验收".into()));
    }
    let available_files = files::Entity::find()
        .filter(files::Column::ProjectId.eq(project_id))
        .filter(files::Column::Status.eq(crate::entity::enums::FileStatus::Available))
        .count(&txn)
        .await?;
    if available_files == 0 {
        return Err(AppError::Conflict(
            "项目至少上传一个可用文件后才能提交验收".into(),
        ));
    }
    let active_uploads = upload_sessions::Entity::find()
        .filter(upload_sessions::Column::ProjectId.eq(project_id))
        .filter(upload_sessions::Column::Status.is_in([
            crate::entity::enums::UploadStatus::Uploading,
            crate::entity::enums::UploadStatus::Merging,
        ]))
        .count(&txn)
        .await?;
    if active_uploads > 0 {
        return Err(AppError::Conflict(
            "项目仍有活动上传会话，不能提交验收".into(),
        ));
    }
    apply_transition(
        &txn,
        me,
        project.clone(),
        Transition {
            to: ProjectStatus::PendingConfirmation,
            action: "SUBMIT",
            next_confirm_side: Some(side),
            history_confirm_side: Some(side),
            reason: None,
        },
    )
    .await?;
    super::notify::enqueue_project_workflow_notice(
        &txn,
        &project,
        super::notify::WorkflowNotice {
            action: "SUBMIT",
            confirm_side: Some(side),
            reason: None,
            latest_submitter_id: None,
            operator: me,
            base_url,
        },
    )
    .await?;
    txn.commit().await?;
    let model = projects::Entity::find_by_id(project_id)
        .one(db)
        .await?
        .ok_or(AppError::NotFound)?;
    Ok(project_json(db, &model).await)
}

async fn decide(
    db: &DatabaseConnection,
    base_url: &str,
    me: &CurrentUser,
    project_id: u64,
    action: &'static str,
    reason: Option<String>,
) -> ApiResult<Value> {
    let txn = db.begin().await?;
    let project = lock_workflow_project(&txn, me, project_id, "project:confirm").await?;
    if project.status != ProjectStatus::PendingConfirmation {
        return Err(AppError::Conflict("项目当前不在待确认状态".into()));
    }
    let side = project
        .confirm_side
        .ok_or_else(|| AppError::Internal("待确认项目缺少确认方".into()))?;
    if side != user_side(me) {
        return Err(AppError::Forbidden);
    }
    let latest_submit = latest_submission(&txn, project_id).await?;
    let to = if action == "CONFIRM" {
        ProjectStatus::Completed
    } else {
        ProjectStatus::InProgress
    };
    apply_transition(
        &txn,
        me,
        project.clone(),
        Transition {
            to,
            action,
            next_confirm_side: None,
            history_confirm_side: Some(side),
            reason: reason.clone(),
        },
    )
    .await?;
    super::notify::enqueue_project_workflow_notice(
        &txn,
        &project,
        super::notify::WorkflowNotice {
            action,
            confirm_side: Some(side),
            reason: reason.as_deref(),
            latest_submitter_id: Some(latest_submit.operator_id),
            operator: me,
            base_url,
        },
    )
    .await?;
    txn.commit().await?;
    let model = projects::Entity::find_by_id(project_id)
        .one(db)
        .await?
        .ok_or(AppError::NotFound)?;
    Ok(project_json(db, &model).await)
}

pub async fn confirm(
    db: &DatabaseConnection,
    base_url: &str,
    me: &CurrentUser,
    project_id: u64,
) -> ApiResult<Value> {
    decide(db, base_url, me, project_id, "CONFIRM", None).await
}

pub async fn reject(
    db: &DatabaseConnection,
    base_url: &str,
    me: &CurrentUser,
    project_id: u64,
    req: &RejectReq,
) -> ApiResult<Value> {
    let reason = req.reason.trim();
    if reason.is_empty() {
        return Err(AppError::BadRequest("驳回原因不能为空".into()));
    }
    if reason.chars().count() > 500 {
        return Err(AppError::BadRequest("驳回原因过长（最多 500 字）".into()));
    }
    decide(
        db,
        base_url,
        me,
        project_id,
        "REJECT",
        Some(reason.to_owned()),
    )
    .await
}

pub async fn withdraw(
    db: &DatabaseConnection,
    base_url: &str,
    me: &CurrentUser,
    project_id: u64,
) -> ApiResult<Value> {
    let txn = db.begin().await?;
    let project = lock_workflow_project(&txn, me, project_id, "project:withdraw").await?;
    if project.status != ProjectStatus::PendingConfirmation {
        return Err(AppError::Conflict("项目当前不在待确认状态".into()));
    }
    let side = project.confirm_side;
    let latest_submit = latest_submission(&txn, project_id).await?;
    let privileged = me.is_internal() && scope::can_view_all(&txn, me.id).await?;
    if latest_submit.operator_id != me.id && !privileged {
        return Err(AppError::Forbidden);
    }
    apply_transition(
        &txn,
        me,
        project.clone(),
        Transition {
            to: ProjectStatus::InProgress,
            action: "WITHDRAW",
            next_confirm_side: None,
            history_confirm_side: side,
            reason: None,
        },
    )
    .await?;
    super::notify::enqueue_project_workflow_notice(
        &txn,
        &project,
        super::notify::WorkflowNotice {
            action: "WITHDRAW",
            confirm_side: side,
            reason: None,
            latest_submitter_id: Some(latest_submit.operator_id),
            operator: me,
            base_url,
        },
    )
    .await?;
    txn.commit().await?;
    let model = projects::Entity::find_by_id(project_id)
        .one(db)
        .await?
        .ok_or(AppError::NotFound)?;
    Ok(project_json(db, &model).await)
}

async fn latest_submission(
    txn: &sea_orm::DatabaseTransaction,
    project_id: u64,
) -> ApiResult<project_status_logs::Model> {
    project_status_logs::Entity::find()
        .filter(project_status_logs::Column::ProjectId.eq(project_id))
        .filter(project_status_logs::Column::Action.eq("SUBMIT"))
        .order_by_desc(project_status_logs::Column::Id)
        .one(txn)
        .await?
        .ok_or_else(|| AppError::Internal("待确认项目缺少提交历史".into()))
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
    let department_ids: Vec<u64> = user_map.values().filter_map(|u| u.department_id).collect();
    let department_map: std::collections::HashMap<u64, String> = if department_ids.is_empty() {
        std::collections::HashMap::new()
    } else {
        departments::Entity::find()
            .filter(departments::Column::Id.is_in(department_ids))
            .all(db)
            .await?
            .into_iter()
            .map(|d| (d.id, d.name))
            .collect()
    };
    let mut list = Vec::with_capacity(rows.len());
    for m in rows {
        if let Some(u) = user_map.get(&m.user_id) {
            list.push(json!({
                "userId": u.id, "employeeNo": u.employee_no, "realName": u.real_name,
                "departmentId": u.department_id,
                "deptName": u.department_id.and_then(|d| department_map.get(&d)),
                "status": if u.status == CommonStatus::Active { "ACTIVE" } else { "DISABLED" },
                "createdAt": m.created_at,
            }));
        }
    }
    Ok(json!(list))
}

/// 项目关联供应商成员：仅展示启用供应商下的启用供应商账号。
pub async fn list_supplier_members(
    db: &DatabaseConnection,
    user: &CurrentUser,
    project_id: u64,
) -> ApiResult<Value> {
    let project = scope::ensure_project_access(db, user, project_id).await?;
    let supplier_active = suppliers::Entity::find_by_id(project.supplier_id)
        .filter(suppliers::Column::Status.eq(CommonStatus::Active))
        .one(db)
        .await?
        .is_some();
    if !supplier_active {
        return Ok(json!([]));
    }
    let rows = users::Entity::find()
        .filter(users::Column::UserType.eq(UserType::Supplier))
        .filter(users::Column::SupplierId.eq(project.supplier_id))
        .filter(users::Column::Status.eq(CommonStatus::Active))
        .order_by_asc(users::Column::Id)
        .all(db)
        .await?;
    Ok(json!(rows
        .iter()
        .map(|u| json!({
            "userId": u.id,
            "employeeNo": u.employee_no,
            "realName": u.real_name,
            "status": "ACTIVE",
        }))
        .collect::<Vec<_>>()))
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
    let project = projects::Entity::find_by_id(project_id)
        .lock_exclusive()
        .one(&txn)
        .await?
        .ok_or(AppError::NotFound)?;
    super::perm::recheck_manager(&txn, me.id, "project:member").await?;
    scope::ensure_project_access(&txn, me, project_id).await?;
    if !matches!(
        project.status,
        ProjectStatus::Draft | ProjectStatus::InProgress
    ) {
        return Err(AppError::Conflict("项目当前状态不可调整成员".into()));
    }
    // 等待期间目标或操作者可能已停用；校验最终完整集合（包括保留的操作者）。
    for uid in &ids {
        let u = users::Entity::find_by_id(*uid)
            .one(&txn)
            .await?
            .ok_or(AppError::BadRequest(format!("用户不存在: {uid}")))?;
        if u.user_type != UserType::Internal || u.status != CommonStatus::Active {
            return Err(AppError::BadRequest(format!(
                "用户 {} 不是启用的内部账号",
                u.employee_no
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
        Some(me.employee_no.clone()),
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

/// 项目徽标：未读留言数 + 是否待我方确认。
pub async fn summary(
    db: &DatabaseConnection,
    user: &CurrentUser,
    project_id: u64,
) -> ApiResult<Value> {
    scope::ensure_project_access(db, user, project_id).await?;
    let unread = crate::service::message::unread_count(db, user.id, project_id).await?;
    let project = projects::Entity::find_by_id(project_id)
        .one(db)
        .await?
        .ok_or(AppError::NotFound)?;
    let pending_confirmation = super::perm::check_perm(db, user.id, "project:confirm")
        .await
        .is_ok()
        && project.status == ProjectStatus::PendingConfirmation
        && project.confirm_side == Some(user_side(user));
    Ok(json!({
        "unreadMessages": unread,
        "pendingConfirmation": pending_confirmation,
    }))
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
        .map(|s| json!({ "id": s.id, "name": s.name }))
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
            "id": u.id, "employeeNo": u.employee_no, "realName": u.real_name,
            "deptName": u.department_id.and_then(|d| dept_map.get(&d)),
        }));
    }
    Ok(json!(out))
}

fn delete_refused_reason(status: ProjectStatus, has_content: bool) -> Option<&'static str> {
    if matches!(
        status,
        ProjectStatus::InProgress | ProjectStatus::PendingConfirmation | ProjectStatus::Completed
    ) {
        return Some("进行中、待确认或已完成的项目不能删除");
    }
    if has_content {
        return Some("项目内仍有文件或留言，不能直接删除");
    }
    None
}

pub async fn delete(state: &AppState, user: &CurrentUser, id: u64) -> ApiResult<()> {
    if !user.is_internal() {
        return Err(AppError::Forbidden);
    }
    let txn = state.db.begin().await?;
    // Same order as member management: management gate -> project row -> authorization.
    // Acquire the project lock before snapshot reads, so a concurrent start cannot pass a stale DRAFT check.
    super::perm::lock_management_state(&txn).await?;
    let project = projects::Entity::find_by_id(id)
        .lock_exclusive()
        .one(&txn)
        .await?
        .ok_or(AppError::NotFound)?;
    super::perm::recheck_manager(&txn, user.id, "project:delete").await?;
    scope::ensure_project_access(&txn, user, id).await?;
    let file_count = files::Entity::find()
        .filter(files::Column::ProjectId.eq(id))
        .count(&txn)
        .await?;
    let message_count = messages::Entity::find()
        .filter(messages::Column::ProjectId.eq(id))
        .count(&txn)
        .await?;
    if let Some(reason) = delete_refused_reason(project.status, file_count + message_count > 0) {
        return Err(AppError::BadRequest(reason.into()));
    }
    if upload_sessions::Entity::find()
        .filter(upload_sessions::Column::ProjectId.eq(id))
        .count(&txn)
        .await?
        > 0
    {
        return Err(AppError::BadRequest("项目仍有上传记录，不能删除".into()));
    }
    // A project with business content is refused above, never cascaded into file/message deletion.
    project_members::Entity::delete_many()
        .filter(project_members::Column::ProjectId.eq(id))
        .exec(&txn)
        .await?;
    email_outbox::Entity::delete_many()
        .filter(email_outbox::Column::ProjectId.eq(id))
        .exec(&txn)
        .await?;
    project_status_logs::Entity::delete_many()
        .filter(project_status_logs::Column::ProjectId.eq(id))
        .exec(&txn)
        .await?;
    audit::insert(
        &txn,
        Some(user.id),
        Some(user.employee_no.clone()),
        "PROJECT_DELETE",
        Some("project"),
        Some(id.to_string()),
        Some(json!({ "name": project.name })),
        None,
    )
    .await?;
    // PROJECT_DELETE 审计仍独立保留；项目本身硬删除前移除时间线以满足 FK 与空项目删除契约。
    project_activities::Entity::delete_many()
        .filter(project_activities::Column::ProjectId.eq(id))
        .exec(&txn)
        .await?;
    let deleted = projects::Entity::delete_by_id(id).exec(&txn).await?;
    if deleted.rows_affected != 1 {
        return Err(AppError::Conflict("项目已被删除，请刷新后重试".into()));
    }
    txn.commit().await?;
    Ok(())
}

#[cfg(test)]
mod tests {
    use crate::entity::enums::ProjectStatus;

    #[test]
    fn in_progress_or_contentful_projects_cannot_be_deleted() {
        use super::delete_refused_reason;
        assert_eq!(
            delete_refused_reason(ProjectStatus::InProgress, false),
            Some("进行中、待确认或已完成的项目不能删除")
        );
        assert_eq!(
            delete_refused_reason(ProjectStatus::Draft, true),
            Some("项目内仍有文件或留言，不能直接删除")
        );
        assert_eq!(delete_refused_reason(ProjectStatus::Draft, false), None);
        assert_eq!(
            delete_refused_reason(ProjectStatus::Terminated, false),
            None
        );
    }

    #[test]
    fn workflow_audit_actions_are_specific() {
        use super::workflow_audit_action;

        for (workflow, audit) in [
            ("START", "PROJECT_START"),
            ("SUBMIT", "PROJECT_SUBMIT"),
            ("CONFIRM", "PROJECT_CONFIRM"),
            ("REJECT", "PROJECT_REJECT"),
            ("WITHDRAW", "PROJECT_WITHDRAW"),
            ("TERMINATE", "PROJECT_TERMINATE"),
            ("RESTART", "PROJECT_RESTART"),
        ] {
            assert_eq!(workflow_audit_action(workflow).unwrap(), audit);
        }
        assert!(workflow_audit_action("STATUS").is_err());
    }
}
