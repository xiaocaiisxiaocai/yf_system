use super::enums::{CommonStatus, DeptKind};
use sea_orm::entity::prelude::*;

#[derive(Clone, Debug, PartialEq, Eq, DeriveEntityModel)]
#[sea_orm(table_name = "departments")]
pub struct Model {
    #[sea_orm(primary_key)]
    pub id: u64,
    pub name: String,
    /// 父级组织，不建 FK，应用层维护树并防循环
    pub parent_id: Option<u64>,
    /// DIVISION / DEPARTMENT / SECTION，由父级推导
    pub kind: DeptKind,
    pub sort_no: i32,
    pub status: CommonStatus,
    pub created_at: DateTimeUtc,
    pub updated_at: DateTimeUtc,
}

#[derive(Copy, Clone, Debug, EnumIter, DeriveRelation)]
pub enum Relation {}

impl ActiveModelBehavior for ActiveModel {}
