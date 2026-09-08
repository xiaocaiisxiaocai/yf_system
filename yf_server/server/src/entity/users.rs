use super::enums::{CommonStatus, UserType};
use sea_orm::entity::prelude::*;

#[derive(Clone, Debug, PartialEq, Eq, DeriveEntityModel)]
#[sea_orm(table_name = "users")]
pub struct Model {
    #[sea_orm(primary_key)]
    pub id: u64,
    /// 登录工号
    pub employee_no: String,
    /// Argon2id 散列
    pub password_hash: String,
    pub real_name: String,
    pub email: String,
    pub phone: Option<String>,
    pub user_type: UserType,
    /// user_type=SUPPLIER 时必填
    pub supplier_id: Option<u64>,
    /// 内部用户所属部门（不建 FK）
    pub department_id: Option<u64>,
    pub status: CommonStatus,
    pub must_change_password: bool,
    pub failed_login_attempts: i32,
    pub locked_until: Option<DateTimeUtc>,
    pub last_login_at: Option<DateTimeUtc>,
    pub last_login_ip: Option<String>,
    pub created_by: Option<u64>,
    pub created_at: DateTimeUtc,
    pub updated_at: DateTimeUtc,
}

#[derive(Copy, Clone, Debug, EnumIter, DeriveRelation)]
pub enum Relation {}

impl ActiveModelBehavior for ActiveModel {}
