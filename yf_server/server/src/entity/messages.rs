use super::enums::MessageStatus;
use sea_orm::entity::prelude::*;

#[derive(Clone, Debug, PartialEq, Eq, DeriveEntityModel)]
#[sea_orm(table_name = "messages")]
pub struct Model {
    #[sea_orm(primary_key)]
    pub id: u64,
    pub project_id: u64,
    pub sender_id: u64,
    #[sea_orm(column_type = "Text")]
    pub content: String,
    pub status: MessageStatus,
    /// 仅管理员可删，留痕
    pub deleted_by: Option<u64>,
    pub deleted_at: Option<DateTimeUtc>,
    pub created_at: DateTimeUtc,
}

#[derive(Copy, Clone, Debug, EnumIter, DeriveRelation)]
pub enum Relation {}

impl ActiveModelBehavior for ActiveModel {}
