use super::enums::RoundStatus;
use sea_orm::entity::prelude::*;

/// 轮次状态变更历史（创建时 from_status 为 NULL）
#[derive(Clone, Debug, PartialEq, Eq, DeriveEntityModel)]
#[sea_orm(table_name = "round_status_logs")]
pub struct Model {
    #[sea_orm(primary_key)]
    pub id: u64,
    pub round_id: u64,
    pub from_status: Option<RoundStatus>,
    pub to_status: RoundStatus,
    pub reason: Option<String>,
    pub operator_id: u64,
    pub created_at: DateTimeUtc,
}

#[derive(Copy, Clone, Debug, EnumIter, DeriveRelation)]
pub enum Relation {}

impl ActiveModelBehavior for ActiveModel {}
