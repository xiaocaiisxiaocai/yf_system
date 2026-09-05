use sea_orm::entity::prelude::*;

/// 留言已读记录（按人）
#[derive(Clone, Debug, PartialEq, Eq, DeriveEntityModel)]
#[sea_orm(table_name = "message_reads")]
pub struct Model {
    #[sea_orm(primary_key, auto_increment = false)]
    pub message_id: u64,
    #[sea_orm(primary_key, auto_increment = false)]
    pub user_id: u64,
    pub read_at: DateTimeUtc,
}

#[derive(Copy, Clone, Debug, EnumIter, DeriveRelation)]
pub enum Relation {}

impl ActiveModelBehavior for ActiveModel {}
