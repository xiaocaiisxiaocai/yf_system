//! 操作日志查询
use sea_orm::{
    ColumnTrait, Condition, DatabaseConnection, EntityTrait, PaginatorTrait, QueryFilter,
    QueryOrder, QuerySelect, TransactionTrait,
};
use serde::Deserialize;
use serde_json::{json, Value};

use crate::dto::PageResp;
use crate::entity::{audit_logs, users};
use crate::error::{ApiResult, AppError};
use crate::middleware::auth::CurrentUser;

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LogQuery {
    #[serde(default = "crate::dto::default_page")]
    pub page: u64,
    #[serde(default = "crate::dto::default_page_size")]
    pub page_size: u64,
    pub keyword: Option<String>,
    pub category: Option<String>,
    pub employee_no: Option<String>,
    pub action: Option<String>,
    pub target_type: Option<String>,
    pub target_id: Option<String>,
    pub start: Option<chrono::DateTime<chrono::Utc>>,
    pub end: Option<chrono::DateTime<chrono::Utc>>,
}

fn actions_for_category(category: &str) -> Option<&'static [&'static str]> {
    match category {
        "AUTH" => Some(&[
            "LOGIN",
            "LOGIN_FAILED",
            "LOGIN_LOCKED",
            "LOGOUT",
            "PASSWORD_CHANGE",
        ]),
        "PROJECT" => Some(&[
            "PROJECT_CREATE",
            "PROJECT_UPDATE",
            "PROJECT_START",
            "PROJECT_SUBMIT",
            "PROJECT_CONFIRM",
            "PROJECT_REJECT",
            "PROJECT_WITHDRAW",
            "PROJECT_TERMINATE",
            "PROJECT_RESTART",
            "PROJECT_MEMBERS",
            "PROJECT_DELETE",
        ]),
        "FILE" => Some(&[
            "FILE_UPLOAD",
            "FILE_DOWNLOAD",
            "FILE_BATCH_DOWNLOAD",
            "FILE_DELETE",
            "UPLOAD_ABORT",
        ]),
        "MESSAGE" => Some(&["MESSAGE_CREATE", "MESSAGE_DELETE"]),
        "ORG" => Some(&[
            "USER_CREATE",
            "USER_UPDATE",
            "USER_STATUS",
            "USER_RESET_PASSWORD",
            "USER_ASSIGN_ROLE",
            "USER_ASSIGN_ROLES",
            "DEPT_CREATE",
            "DEPT_UPDATE",
            "DEPT_STATUS",
            "DEPT_DELETE",
            "USER_DELETE",
            "ROLE_CREATE",
            "ROLE_UPDATE",
            "ROLE_STATUS",
            "ROLE_ASSIGN_PERMS",
            "ROLE_DELETE",
        ]),
        "SUPPLIER" => Some(&[
            "SUPPLIER_CREATE",
            "SUPPLIER_UPDATE",
            "SUPPLIER_STATUS",
            "SUPPLIER_DELETE",
            "SUPPLIER_ACCOUNT_CREATE",
            "SUPPLIER_ACCOUNT_UPDATE",
            "SUPPLIER_ACCOUNT_STATUS",
            "SUPPLIER_ACCOUNT_RESET_PASSWORD",
            "SUPPLIER_ACCOUNT_DELETE",
        ]),
        "SYSTEM" => Some(&[
            "CONFIG_UPDATE",
            "AUDIT_LOG_DELETE",
            "EMAIL_SENT",
            "EMAIL_FAILED",
            "EMAIL_RETRY",
            "EMAIL_SKIPPED_MISSING_EMAIL",
        ]),
        _ => None,
    }
}

pub async fn list(db: &DatabaseConnection, q: &LogQuery) -> ApiResult<PageResp<Value>> {
    let (page, size) = crate::dto::clamp_page(q.page, q.page_size);
    let mut cond = Condition::all();
    if let Some(keyword) = q
        .keyword
        .as_ref()
        .map(|value| value.trim())
        .filter(|value| !value.is_empty())
    {
        cond = cond.add(
            Condition::any()
                .add(audit_logs::Column::EmployeeNo.contains(keyword))
                .add(audit_logs::Column::Action.contains(keyword))
                .add(audit_logs::Column::TargetType.contains(keyword))
                .add(audit_logs::Column::TargetId.contains(keyword)),
        );
    }
    if let Some(actions) = q.category.as_deref().and_then(actions_for_category) {
        cond = cond.add(audit_logs::Column::Action.is_in(actions.iter().copied()));
    }
    if let Some(u) = q.employee_no.as_ref().filter(|s| !s.trim().is_empty()) {
        cond = cond.add(audit_logs::Column::EmployeeNo.contains(u.trim()));
    }
    if let Some(a) = q.action.as_ref().filter(|s| !s.trim().is_empty()) {
        cond = cond.add(audit_logs::Column::Action.eq(a.trim()));
    }
    if let Some(t) = q.target_type.as_ref().filter(|s| !s.trim().is_empty()) {
        cond = cond.add(audit_logs::Column::TargetType.eq(t.trim()));
    }
    if let Some(id) = q.target_id.as_ref().filter(|s| !s.trim().is_empty()) {
        cond = cond.add(audit_logs::Column::TargetId.eq(id.trim()));
    }
    if let Some(s) = q.start {
        cond = cond.add(audit_logs::Column::CreatedAt.gte(s));
    }
    if let Some(e) = q.end {
        cond = cond.add(audit_logs::Column::CreatedAt.lte(e));
    }
    let paginator = audit_logs::Entity::find()
        .filter(cond)
        .order_by_desc(audit_logs::Column::Id)
        .paginate(db, size);
    let total = paginator.num_items().await?;
    let items = paginator.fetch_page(page - 1).await?;
    let user_ids: Vec<u64> = items.iter().filter_map(|item| item.user_id).collect();
    let current_employee_nos: std::collections::HashMap<u64, String> = if user_ids.is_empty() {
        std::collections::HashMap::new()
    } else {
        users::Entity::find()
            .filter(users::Column::Id.is_in(user_ids))
            .all(db)
            .await?
            .into_iter()
            .map(|user| (user.id, user.employee_no))
            .collect()
    };
    let list = items
        .iter()
        .map(|l| {
            let employee_no = l.employee_no.clone().or_else(|| {
                l.user_id
                    .and_then(|id| current_employee_nos.get(&id).cloned())
            });
            json!({
                "id": l.id, "userId": l.user_id, "employeeNo": employee_no,
                "action": l.action, "targetType": l.target_type, "targetId": l.target_id,
                "detail": l.detail, "ip": l.ip, "createdAt": l.created_at,
            })
        })
        .collect();
    Ok(PageResp::new(list, total, page, size))
}

pub async fn delete_ids(
    db: &DatabaseConnection,
    user: &CurrentUser,
    ids: &[u64],
) -> ApiResult<u64> {
    if ids.is_empty() || ids.len() > 500 {
        return Err(crate::error::AppError::BadRequest(
            "请选择 1~500 条日志".into(),
        ));
    }
    let txn = db.begin().await?;
    super::perm::lock_management_state(&txn).await?;
    super::perm::recheck_manager(&txn, user.id, "log:delete").await?;
    let rows = audit_logs::Entity::find()
        .filter(audit_logs::Column::Id.is_in(ids.to_vec()))
        .order_by_asc(audit_logs::Column::Id)
        .lock_exclusive()
        .all(&txn)
        .await?;
    if rows.iter().any(|row| row.action == "AUDIT_LOG_DELETE") {
        return Err(AppError::BadRequest(
            "日志清理记录必须保留，不能删除".into(),
        ));
    }
    let actual_ids: Vec<u64> = rows.iter().map(|row| row.id).collect();
    if actual_ids.is_empty() {
        txn.commit().await?;
        return Ok(0);
    }
    let result = audit_logs::Entity::delete_many()
        .filter(audit_logs::Column::Id.is_in(actual_ids.clone()))
        .exec(&txn)
        .await?;
    super::audit::insert(
        &txn,
        Some(user.id),
        Some(user.employee_no.clone()),
        "AUDIT_LOG_DELETE",
        Some("audit_log"),
        None,
        Some(json!({ "ids": actual_ids, "deleted": result.rows_affected })),
        None,
    )
    .await?;
    txn.commit().await?;
    Ok(result.rows_affected)
}

#[cfg(test)]
mod tests {
    use super::actions_for_category;

    #[test]
    fn audit_categories_cover_role_assignment_and_reject_unknown_values() {
        let project = actions_for_category("PROJECT").expect("项目分类应存在");
        for action in [
            "PROJECT_START",
            "PROJECT_SUBMIT",
            "PROJECT_CONFIRM",
            "PROJECT_REJECT",
            "PROJECT_WITHDRAW",
            "PROJECT_TERMINATE",
            "PROJECT_RESTART",
        ] {
            assert!(project.contains(&action), "项目分类缺少 {action}");
        }
        assert!(!project.contains(&"PROJECT_STATUS"));
        let org = actions_for_category("ORG").expect("组织分类应存在");
        assert!(org.contains(&"USER_ASSIGN_ROLE"));
        let supplier = actions_for_category("SUPPLIER").expect("供应商分类应存在");
        assert!(supplier.contains(&"SUPPLIER_ACCOUNT_STATUS"));
        let system = actions_for_category("SYSTEM").expect("系统分类应存在");
        assert!(system.contains(&"EMAIL_SENT"));
        assert!(system.contains(&"EMAIL_FAILED"));
        assert!(system.contains(&"EMAIL_RETRY"));
        assert!(system.contains(&"EMAIL_SKIPPED_MISSING_EMAIL"));
        assert!(actions_for_category("UNKNOWN").is_none());
    }
}
