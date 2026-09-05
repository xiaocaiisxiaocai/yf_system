//! 操作日志查询
use sea_orm::{
    ColumnTrait, Condition, DatabaseConnection, EntityTrait, PaginatorTrait, QueryFilter,
    QueryOrder,
};
use serde::Deserialize;
use serde_json::{json, Value};

use crate::dto::PageResp;
use crate::entity::audit_logs;
use crate::error::ApiResult;

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LogQuery {
    #[serde(default = "crate::dto::default_page")]
    pub page: u64,
    #[serde(default = "crate::dto::default_page_size")]
    pub page_size: u64,
    pub keyword: Option<String>,
    pub category: Option<String>,
    pub username: Option<String>,
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
            "PROJECT_STATUS",
            "PROJECT_MEMBERS",
            "ROUND_CREATE",
            "ROUND_CONFIRM",
            "ROUND_REJECT",
            "ROUND_CANCEL",
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
            "ROLE_CREATE",
            "ROLE_UPDATE",
            "ROLE_STATUS",
            "ROLE_ASSIGN_PERMS",
        ]),
        "SUPPLIER" => Some(&[
            "SUPPLIER_CREATE",
            "SUPPLIER_UPDATE",
            "SUPPLIER_STATUS",
            "SUPPLIER_ACCOUNT_CREATE",
            "SUPPLIER_ACCOUNT_UPDATE",
            "SUPPLIER_ACCOUNT_STATUS",
            "SUPPLIER_ACCOUNT_RESET_PASSWORD",
        ]),
        "SYSTEM" => Some(&["CONFIG_UPDATE"]),
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
                .add(audit_logs::Column::Username.contains(keyword))
                .add(audit_logs::Column::Action.contains(keyword))
                .add(audit_logs::Column::TargetType.contains(keyword))
                .add(audit_logs::Column::TargetId.contains(keyword)),
        );
    }
    if let Some(actions) = q.category.as_deref().and_then(actions_for_category) {
        cond = cond.add(audit_logs::Column::Action.is_in(actions.iter().copied()));
    }
    if let Some(u) = q.username.as_ref().filter(|s| !s.trim().is_empty()) {
        cond = cond.add(audit_logs::Column::Username.contains(u.trim()));
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
    let list = items
        .iter()
        .map(|l| {
            json!({
                "id": l.id, "userId": l.user_id, "username": l.username,
                "action": l.action, "targetType": l.target_type, "targetId": l.target_id,
                "detail": l.detail, "ip": l.ip, "createdAt": l.created_at,
            })
        })
        .collect();
    Ok(PageResp::new(list, total, page, size))
}

#[cfg(test)]
mod tests {
    use super::actions_for_category;

    #[test]
    fn audit_categories_cover_role_assignment_and_reject_unknown_values() {
        let org = actions_for_category("ORG").expect("组织分类应存在");
        assert!(org.contains(&"USER_ASSIGN_ROLE"));
        let supplier = actions_for_category("SUPPLIER").expect("供应商分类应存在");
        assert!(supplier.contains(&"SUPPLIER_ACCOUNT_STATUS"));
        assert!(actions_for_category("UNKNOWN").is_none());
    }
}
