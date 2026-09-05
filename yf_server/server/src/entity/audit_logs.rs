use sea_orm::entity::prelude::*;

/// 操作日志（登录/上传/下载/状态变更/留言等）。username 为快照，
/// 账号禁用或改名后日志仍可读。保留 ≥1 年，超期导出归档。
#[derive(Clone, Debug, PartialEq, DeriveEntityModel)]
#[sea_orm(table_name = "audit_logs")]
pub struct Model {
    #[sea_orm(primary_key)]
    pub id: u64,
    /// 未登录动作（如登录失败）为 NULL
    pub user_id: Option<u64>,
    pub username: Option<String>,
    pub action: String,
    pub target_type: Option<String>,
    pub target_id: Option<String>,
    pub detail: Option<Json>,
    pub ip: Option<String>,
    pub created_at: DateTimeUtc,
}

#[derive(Copy, Clone, Debug, EnumIter, DeriveRelation)]
pub enum Relation {}

impl ActiveModelBehavior for ActiveModel {}
