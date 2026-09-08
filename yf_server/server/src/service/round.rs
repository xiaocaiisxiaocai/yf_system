//! 轮次：自由创建 + 确认/驳回/撤销，状态机与历史（《02-数据库设计》§4.3）
use chrono::Utc;
use sea_orm::{
    ActiveModelTrait, ColumnTrait, DatabaseConnection, EntityTrait, QueryFilter, QueryOrder,
    QuerySelect, Set, TransactionTrait,
};
use serde::Deserialize;
use serde_json::{json, Value};

use crate::config::Config;
use crate::entity::enums::{ConfirmSide, ProjectStatus, RoundStatus, UserType};
use crate::entity::{projects, round_status_logs, rounds, users};
use crate::error::{ApiResult, AppError};
use crate::middleware::auth::CurrentUser;

use super::{audit, notify, scope};

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RoundCreate {
    pub title: Option<String>,
    pub remark: Option<String>,
    pub confirm_side: String,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RejectReq {
    /// 必填但允许空对象反序列化，统一由 service 校验并返回 400 业务错误
    pub reason: Option<String>,
}

fn status_str(s: &RoundStatus) -> &'static str {
    match s {
        RoundStatus::Pending => "PENDING",
        RoundStatus::Confirmed => "CONFIRMED",
        RoundStatus::Rejected => "REJECTED",
        RoundStatus::Cancelled => "CANCELLED",
    }
}

fn side_str(s: &ConfirmSide) -> &'static str {
    match s {
        ConfirmSide::Company => "COMPANY",
        ConfirmSide::Supplier => "SUPPLIER",
    }
}

pub fn round_json(
    r: &rounds::Model,
    creator_name: Option<String>,
    decider_name: Option<String>,
) -> Value {
    json!({
        "id": r.id, "projectId": r.project_id, "roundNo": r.round_no,
        "title": r.title, "remark": r.remark,
        "confirmSide": side_str(&r.confirm_side),
        "status": status_str(&r.status),
        "decidedBy": r.decided_by, "decidedByName": decider_name,
        "decidedAt": r.decided_at, "rejectReason": r.reject_reason,
        "createdBy": r.created_by, "createdByName": creator_name,
        "createdAt": r.created_at,
    })
}

async fn name_of(db: &DatabaseConnection, id: Option<u64>) -> Option<String> {
    match id {
        Some(id) => users::Entity::find_by_id(id)
            .one(db)
            .await
            .ok()
            .flatten()
            .map(|u| u.real_name),
        None => None,
    }
}

pub async fn list(
    db: &DatabaseConnection,
    user: &CurrentUser,
    project_id: u64,
) -> ApiResult<Value> {
    scope::ensure_project_access(db, user, project_id).await?;
    let rows = rounds::Entity::find()
        .filter(rounds::Column::ProjectId.eq(project_id))
        .order_by_asc(rounds::Column::RoundNo)
        .all(db)
        .await?;
    // 批量取发起人/确认人姓名，避免逐轮查询
    let ids: Vec<u64> = rows
        .iter()
        .flat_map(|r| [Some(r.created_by), r.decided_by])
        .flatten()
        .collect();
    let name_map: std::collections::HashMap<u64, String> = users::Entity::find()
        .filter(users::Column::Id.is_in(ids))
        .all(db)
        .await?
        .into_iter()
        .map(|u| (u.id, u.real_name))
        .collect();
    let mut list = Vec::with_capacity(rows.len());
    for r in &rows {
        list.push(round_json(
            r,
            name_map.get(&r.created_by).cloned(),
            r.decided_by.and_then(|d| name_map.get(&d).cloned()),
        ));
    }
    Ok(json!(list))
}

/// 创建轮次：仅内部用户；项目须进行中；事务内锁项目行分配 round_no
pub async fn create(
    db: &DatabaseConnection,
    me: &CurrentUser,
    project_id: u64,
    req: &RoundCreate,
) -> ApiResult<Value> {
    if !me.is_internal() {
        return Err(AppError::Forbidden);
    }
    let confirm_side = match req.confirm_side.as_str() {
        "COMPANY" => ConfirmSide::Company,
        "SUPPLIER" => ConfirmSide::Supplier,
        _ => {
            return Err(AppError::BadRequest(
                "confirmSide 必须是 COMPANY 或 SUPPLIER".into(),
            ))
        }
    };
    if req
        .title
        .as_deref()
        .map(|t| t.trim().chars().count())
        .unwrap_or(0)
        > 128
    {
        return Err(AppError::BadRequest("轮次标题过长（最多 128 字）".into()));
    }
    if req
        .remark
        .as_deref()
        .map(|t| t.chars().count())
        .unwrap_or(0)
        > 500
    {
        return Err(AppError::BadRequest("轮次备注过长（最多 500 字）".into()));
    }
    let txn = db.begin().await?;
    let project = projects::Entity::find_by_id(project_id)
        .lock_exclusive()
        .one(&txn)
        .await?
        .ok_or(AppError::NotFound)?;
    scope::ensure_project_access(&txn, me, project_id).await?;
    if project.status != ProjectStatus::InProgress {
        return Err(AppError::Conflict(
            "项目需处于「进行中」才能创建轮次".into(),
        ));
    }

    // 锁项目行，串行化同项目的 round_no 分配（另有 UNIQUE(project_id, round_no) 双保险）
    let max_no: Option<i32> = rounds::Entity::find()
        .filter(rounds::Column::ProjectId.eq(project_id))
        .order_by_desc(rounds::Column::RoundNo)
        .one(&txn)
        .await?
        .map(|r| r.round_no);
    let round_no = max_no.unwrap_or(0) + 1;

    let now = Utc::now();
    let model = rounds::ActiveModel {
        project_id: Set(project_id),
        round_no: Set(round_no),
        title: Set(req.title.clone()),
        remark: Set(req.remark.clone()),
        confirm_side: Set(confirm_side),
        status: Set(RoundStatus::Pending),
        decided_by: Set(None),
        decided_at: Set(None),
        reject_reason: Set(None),
        created_by: Set(me.id),
        created_at: Set(now),
        updated_at: Set(now),
        ..Default::default()
    }
    .insert(&txn)
    .await?;
    round_status_logs::ActiveModel {
        round_id: Set(model.id),
        from_status: Set(None),
        to_status: Set(RoundStatus::Pending),
        reason: Set(None),
        operator_id: Set(me.id),
        created_at: Set(now),
        ..Default::default()
    }
    .insert(&txn)
    .await?;
    audit::insert(
        &txn,
        Some(me.id),
        Some(me.employee_no.clone()),
        "ROUND_CREATE",
        Some("round"),
        Some(model.id.to_string()),
        Some(json!({"projectId": project_id, "roundNo": round_no})),
        None,
    )
    .await?;
    txn.commit().await?;
    Ok(round_json(&model, Some(me.employee_no.clone()), None))
}

pub async fn detail(db: &DatabaseConnection, user: &CurrentUser, id: u64) -> ApiResult<Value> {
    let r = rounds::Entity::find_by_id(id)
        .one(db)
        .await?
        .ok_or(AppError::NotFound)?;
    scope::ensure_project_access(db, user, r.project_id).await?;
    let logs = round_status_logs::Entity::find()
        .filter(round_status_logs::Column::RoundId.eq(id))
        .order_by_asc(round_status_logs::Column::Id)
        .all(db)
        .await?;
    // 批量取相关工号（发起人/确认人/各日志操作人）
    let ids: Vec<u64> = std::iter::once(r.created_by)
        .chain(r.decided_by)
        .chain(logs.iter().map(|l| l.operator_id))
        .collect();
    let name_map: std::collections::HashMap<u64, String> = users::Entity::find()
        .filter(users::Column::Id.is_in(ids))
        .all(db)
        .await?
        .into_iter()
        .map(|u| (u.id, u.real_name))
        .collect();
    let mut v = round_json(
        &r,
        name_map.get(&r.created_by).cloned(),
        r.decided_by.and_then(|d| name_map.get(&d).cloned()),
    );
    let mut log_list = Vec::with_capacity(logs.len());
    for l in &logs {
        log_list.push(json!({
            "id": l.id,
            "fromStatus": l.from_status.as_ref().map(status_str),
            "toStatus": status_str(&l.to_status),
            "reason": l.reason,
            "operatorId": l.operator_id,
            "operatorName": name_map.get(&l.operator_id).cloned(),
            "createdAt": l.created_at,
        }));
    }
    v["logs"] = json!(log_list);
    Ok(v)
}

/// 确认方校验：confirmSide=COMPANY → 内部；SUPPLIER → 本项目供应商人员
fn check_side(r: &rounds::Model, user: &CurrentUser, project_supplier_id: u64) -> ApiResult<()> {
    match r.confirm_side {
        ConfirmSide::Company if user.user_type == UserType::Internal => Ok(()),
        ConfirmSide::Supplier
            if user.user_type == UserType::Supplier
                && user.supplier_id == Some(project_supplier_id) =>
        {
            Ok(())
        }
        ConfirmSide::Company => Err(AppError::Forbidden),
        ConfirmSide::Supplier => Err(AppError::Forbidden),
    }
}

async fn transit(
    db: &DatabaseConnection,
    cfg: &Config,
    user: &CurrentUser,
    id: u64,
    to: RoundStatus,
    reason: Option<String>,
    action: &str,
) -> ApiResult<Value> {
    let r = rounds::Entity::find_by_id(id)
        .one(db)
        .await?
        .ok_or(AppError::NotFound)?;
    // 所有写路径统一项目 → 轮次，避免通知外键检查与上传形成反向锁依赖。
    let txn = db.begin().await?;
    let project = projects::Entity::find_by_id(r.project_id)
        .lock_exclusive()
        .one(&txn)
        .await?
        .ok_or(AppError::NotFound)?;
    let r = rounds::Entity::find_by_id(id)
        .lock_exclusive()
        .one(&txn)
        .await?
        .ok_or(AppError::NotFound)?;
    scope::ensure_project_access(&txn, user, r.project_id).await?;
    if r.status != RoundStatus::Pending {
        return Err(AppError::Conflict("轮次当前状态不可操作".into()));
    }
    if matches!(to, RoundStatus::Confirmed | RoundStatus::Rejected) {
        check_side(&r, user, project.supplier_id)?;
    }
    if to == RoundStatus::Rejected {
        match &reason {
            Some(r) if !r.trim().is_empty() => {
                if r.trim().chars().count() > 500 {
                    return Err(AppError::BadRequest("驳回原因过长（最多 500 字）".into()));
                }
            }
            _ => return Err(AppError::BadRequest("驳回必须填写原因".into())),
        }
    }
    if to == RoundStatus::Cancelled && !user.is_internal() {
        return Err(AppError::Forbidden);
    }
    if to == RoundStatus::Cancelled
        && r.created_by != user.id
        && !scope::can_view_all(&txn, user.id).await?
    {
        return Err(AppError::Forbidden);
    }

    let from = r.status;
    let round_no = r.round_no;
    let now = Utc::now();
    let mut am: rounds::ActiveModel = r.into();
    am.status = Set(to);
    am.updated_at = Set(now);
    if matches!(to, RoundStatus::Confirmed | RoundStatus::Rejected) {
        am.decided_by = Set(Some(user.id));
        am.decided_at = Set(Some(now));
        am.reject_reason = Set(if to == RoundStatus::Rejected {
            reason.clone()
        } else {
            None
        });
    }
    // CAS 流转：仅当状态仍为 PENDING 时才更新，防并发下两人同时确认/驳回成功
    let upd = rounds::Entity::update_many()
        .set(am)
        .filter(rounds::Column::Id.eq(id))
        .filter(rounds::Column::Status.eq(RoundStatus::Pending))
        .exec(&txn)
        .await?;
    if upd.rows_affected == 0 {
        txn.rollback().await?;
        return Err(AppError::Conflict(
            "轮次状态已被他人变更，请刷新后重试".into(),
        ));
    }
    round_status_logs::ActiveModel {
        round_id: Set(id),
        from_status: Set(Some(from)),
        to_status: Set(to),
        reason: Set(reason.clone()),
        operator_id: Set(user.id),
        created_at: Set(now),
        ..Default::default()
    }
    .insert(&txn)
    .await?;
    notify::enqueue_round_notice(
        &txn,
        &project,
        notify::RoundNotice {
            round_id: id,
            round_no,
            to,
            reason: reason.as_deref(),
            operator: user,
            base_url: &cfg.web.base_url,
        },
    )
    .await?;
    audit::insert(
        &txn,
        Some(user.id),
        Some(user.employee_no.clone()),
        action,
        Some("round"),
        Some(id.to_string()),
        Some(json!({"to": status_str(&to), "reason": reason})),
        None,
    )
    .await?;
    txn.commit().await?;
    let model = rounds::Entity::find_by_id(id)
        .one(db)
        .await?
        .ok_or(AppError::NotFound)?;
    Ok(round_json(
        &model,
        name_of(db, Some(model.created_by)).await,
        name_of(db, model.decided_by).await,
    ))
}

pub async fn confirm(
    db: &DatabaseConnection,
    cfg: &Config,
    user: &CurrentUser,
    id: u64,
) -> ApiResult<Value> {
    transit(
        db,
        cfg,
        user,
        id,
        RoundStatus::Confirmed,
        None,
        "ROUND_CONFIRM",
    )
    .await
}

pub async fn reject(
    db: &DatabaseConnection,
    cfg: &Config,
    user: &CurrentUser,
    id: u64,
    req: &RejectReq,
) -> ApiResult<Value> {
    let reason = req
        .reason
        .as_deref()
        .map(str::trim)
        .unwrap_or("")
        .to_string();
    transit(
        db,
        cfg,
        user,
        id,
        RoundStatus::Rejected,
        Some(reason),
        "ROUND_REJECT",
    )
    .await
}

pub async fn cancel(
    db: &DatabaseConnection,
    cfg: &Config,
    user: &CurrentUser,
    id: u64,
) -> ApiResult<Value> {
    transit(
        db,
        cfg,
        user,
        id,
        RoundStatus::Cancelled,
        None,
        "ROUND_CANCEL",
    )
    .await
}
