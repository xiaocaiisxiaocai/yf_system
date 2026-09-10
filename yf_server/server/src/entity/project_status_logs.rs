use super::enums::{ConfirmSide, ProjectStatus};
use sea_orm::entity::prelude::*;

/// Authoritative project state history. Every state transition is appended transactionally.
#[derive(Clone, Debug, PartialEq, Eq, DeriveEntityModel)]
#[sea_orm(table_name = "project_status_logs")]
pub struct Model {
    #[sea_orm(primary_key)]
    pub id: u64,
    pub project_id: u64,
    pub from_status: Option<ProjectStatus>,
    pub to_status: ProjectStatus,
    pub action: String,
    pub operator_id: u64,
    pub confirm_side: Option<ConfirmSide>,
    pub reason: Option<String>,
    pub created_at: DateTimeUtc,
}

#[derive(Copy, Clone, Debug, EnumIter, DeriveRelation)]
pub enum Relation {}

impl ActiveModelBehavior for ActiveModel {}
