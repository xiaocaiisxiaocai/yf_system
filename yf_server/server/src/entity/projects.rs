use super::enums::ProjectStatus;
use sea_orm::entity::prelude::*;

#[derive(Clone, Debug, PartialEq, Eq, DeriveEntityModel)]
#[sea_orm(table_name = "projects")]
pub struct Model {
    #[sea_orm(primary_key)]
    pub id: u64,
    pub name: String,
    pub description: Option<String>,
    /// 一对一关联供应商
    pub supplier_id: u64,
    pub status: ProjectStatus,
    pub created_by: u64,
    pub created_at: DateTimeUtc,
    pub updated_at: DateTimeUtc,
}

#[derive(Copy, Clone, Debug, EnumIter, DeriveRelation)]
pub enum Relation {}

impl ActiveModelBehavior for ActiveModel {}
