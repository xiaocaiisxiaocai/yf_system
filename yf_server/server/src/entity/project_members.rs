use sea_orm::entity::prelude::*;

/// 项目内部参与人（仅内部用户，应用层校验）
#[derive(Clone, Debug, PartialEq, Eq, DeriveEntityModel)]
#[sea_orm(table_name = "project_members")]
pub struct Model {
    #[sea_orm(primary_key, auto_increment = false)]
    pub project_id: u64,
    #[sea_orm(primary_key, auto_increment = false)]
    pub user_id: u64,
    pub created_by: Option<u64>,
    pub created_at: DateTimeUtc,
}

#[derive(Copy, Clone, Debug, EnumIter, DeriveRelation)]
pub enum Relation {}

impl ActiveModelBehavior for ActiveModel {}
