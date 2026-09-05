use super::enums::PermissionType;
use sea_orm::entity::prelude::*;

#[derive(Clone, Debug, PartialEq, Eq, DeriveEntityModel)]
#[sea_orm(table_name = "permissions")]
pub struct Model {
    #[sea_orm(primary_key)]
    pub id: u64,
    /// 如 project:create / file:download
    pub code: String,
    pub name: String,
    #[sea_orm(column_name = "type")]
    pub perm_type: PermissionType,
    /// 菜单树父节点
    pub parent_id: Option<u64>,
    pub sort_no: i32,
}

#[derive(Copy, Clone, Debug, EnumIter, DeriveRelation)]
pub enum Relation {}

impl ActiveModelBehavior for ActiveModel {}
