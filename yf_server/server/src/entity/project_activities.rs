use sea_orm::entity::prelude::*;

/// 项目业务时间线。新事件保存操作人名称快照，历史回填使用现存名称。
#[derive(Clone, Debug, PartialEq, Eq, DeriveEntityModel)]
#[sea_orm(table_name = "project_activities")]
pub struct Model {
    #[sea_orm(primary_key)]
    pub id: u64,
    pub project_id: u64,
    pub activity_type: String,
    pub action: String,
    pub actor_id: Option<u64>,
    pub actor_name: String,
    pub occurred_at: DateTimeUtc,
    pub title: String,
    pub summary: Option<String>,
    pub target_id: Option<u64>,
    /// 来自业务记录或审计记录的稳定去重键。
    pub source_key: String,
}

#[derive(Copy, Clone, Debug, EnumIter, DeriveRelation)]
pub enum Relation {}

impl ActiveModelBehavior for ActiveModel {}
