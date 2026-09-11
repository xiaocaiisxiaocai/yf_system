use sea_orm::entity::prelude::*;

/// JWT refresh token（只存 sha256，可吊销：登出/改密/禁用账号）
#[derive(Clone, Debug, PartialEq, Eq, DeriveEntityModel)]
#[sea_orm(table_name = "refresh_tokens")]
pub struct Model {
    #[sea_orm(primary_key)]
    pub id: u64,
    pub user_id: u64,
    /// Stable session family shared by every refresh-token rotation.
    pub session_id: String,
    pub token_hash: String,
    pub expires_at: DateTimeUtc,
    pub revoked: bool,
    pub ip: Option<String>,
    pub created_at: DateTimeUtc,
}

#[derive(Copy, Clone, Debug, EnumIter, DeriveRelation)]
pub enum Relation {}

impl ActiveModelBehavior for ActiveModel {}
