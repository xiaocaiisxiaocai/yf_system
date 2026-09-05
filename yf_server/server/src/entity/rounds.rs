use super::enums::{ConfirmSide, RoundStatus};
use sea_orm::entity::prelude::*;

#[derive(Clone, Debug, PartialEq, Eq, DeriveEntityModel)]
#[sea_orm(table_name = "rounds")]
pub struct Model {
    #[sea_orm(primary_key)]
    pub id: u64,
    pub project_id: u64,
    /// 项目内序号，事务内锁项目行分配，UNIQUE(project_id, round_no) 双保险
    pub round_no: i32,
    pub title: Option<String>,
    pub remark: Option<String>,
    /// 待确认方：确认/驳回只能由该方执行
    pub confirm_side: ConfirmSide,
    pub status: RoundStatus,
    pub decided_by: Option<u64>,
    pub decided_at: Option<DateTimeUtc>,
    /// 驳回必填
    pub reject_reason: Option<String>,
    /// 仅内部用户可创建轮次
    pub created_by: u64,
    pub created_at: DateTimeUtc,
    pub updated_at: DateTimeUtc,
}

#[derive(Copy, Clone, Debug, EnumIter, DeriveRelation)]
pub enum Relation {}

impl ActiveModelBehavior for ActiveModel {}
