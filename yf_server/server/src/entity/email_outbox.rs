use super::enums::{OutboxEventType, OutboxStatus};
use sea_orm::entity::prelude::*;

/// 邮件异步队列：业务事务内写入，worker 轮询发送（指数退避重试）
#[derive(Clone, Debug, PartialEq, Eq, DeriveEntityModel)]
#[sea_orm(table_name = "email_outbox")]
pub struct Model {
    #[sea_orm(primary_key)]
    pub id: u64,
    pub event_type: OutboxEventType,
    /// 系统级通知（如磁盘容量告警）不归属具体项目。
    pub project_id: Option<u64>,
    /// 幂等键；业务通知为空，周期性系统通知使用唯一键防止多实例重复入队。
    pub dedupe_key: Option<String>,
    pub recipient_user_id: Option<u64>,
    pub recipient_email: String,
    pub subject: String,
    /// 通知文案 + 登录链接，不含附件
    #[sea_orm(column_type = "Text")]
    pub body: String,
    pub status: OutboxStatus,
    pub retry_count: i32,
    /// 下一次允许发送的时间；失败后指数退避
    pub next_attempt_at: Option<DateTimeUtc>,
    pub last_error: Option<String>,
    pub sent_at: Option<DateTimeUtc>,
    pub created_at: DateTimeUtc,
}

#[derive(Copy, Clone, Debug, EnumIter, DeriveRelation)]
pub enum Relation {}

impl ActiveModelBehavior for ActiveModel {}
