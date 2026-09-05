//! 全库共享枚举。DeriveActiveEnum → 库存 VARCHAR，值为全大写蛇形。
use sea_orm::entity::prelude::*;

#[derive(Debug, Clone, Copy, PartialEq, Eq, EnumIter, DeriveActiveEnum)]
#[sea_orm(rs_type = "String", db_type = "String(StringLen::N(16))")]
pub enum CommonStatus {
    #[sea_orm(string_value = "ACTIVE")]
    Active,
    #[sea_orm(string_value = "DISABLED")]
    Disabled,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, EnumIter, DeriveActiveEnum)]
#[sea_orm(rs_type = "String", db_type = "String(StringLen::N(16))")]
pub enum UserType {
    #[sea_orm(string_value = "INTERNAL")]
    Internal,
    #[sea_orm(string_value = "SUPPLIER")]
    Supplier,
}

impl UserType {
    pub fn as_str(&self) -> &'static str {
        match self {
            Self::Internal => "INTERNAL",
            Self::Supplier => "SUPPLIER",
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, EnumIter, DeriveActiveEnum)]
#[sea_orm(rs_type = "String", db_type = "String(StringLen::N(16))")]
pub enum ProjectStatus {
    #[sea_orm(string_value = "DRAFT")]
    Draft,
    #[sea_orm(string_value = "IN_PROGRESS")]
    InProgress,
    #[sea_orm(string_value = "COMPLETED")]
    Completed,
    #[sea_orm(string_value = "TERMINATED")]
    Terminated,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, EnumIter, DeriveActiveEnum)]
#[sea_orm(rs_type = "String", db_type = "String(StringLen::N(16))")]
pub enum ConfirmSide {
    #[sea_orm(string_value = "COMPANY")]
    Company,
    #[sea_orm(string_value = "SUPPLIER")]
    Supplier,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, EnumIter, DeriveActiveEnum)]
#[sea_orm(rs_type = "String", db_type = "String(StringLen::N(16))")]
pub enum RoundStatus {
    #[sea_orm(string_value = "PENDING")]
    Pending,
    #[sea_orm(string_value = "CONFIRMED")]
    Confirmed,
    #[sea_orm(string_value = "REJECTED")]
    Rejected,
    #[sea_orm(string_value = "CANCELLED")]
    Cancelled,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, EnumIter, DeriveActiveEnum)]
#[sea_orm(rs_type = "String", db_type = "String(StringLen::N(8))")]
pub enum FileDirection {
    #[sea_orm(string_value = "C2S")]
    C2s,
    #[sea_orm(string_value = "S2C")]
    S2c,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, EnumIter, DeriveActiveEnum)]
#[sea_orm(rs_type = "String", db_type = "String(StringLen::N(16))")]
pub enum FileStatus {
    #[sea_orm(string_value = "AVAILABLE")]
    Available,
    #[sea_orm(string_value = "DELETED")]
    Deleted,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, EnumIter, DeriveActiveEnum)]
#[sea_orm(rs_type = "String", db_type = "String(StringLen::N(16))")]
pub enum UploadStatus {
    #[sea_orm(string_value = "UPLOADING")]
    Uploading,
    #[sea_orm(string_value = "MERGING")]
    Merging,
    #[sea_orm(string_value = "COMPLETED")]
    Completed,
    #[sea_orm(string_value = "ABORTED")]
    Aborted,
    #[sea_orm(string_value = "EXPIRED")]
    Expired,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, EnumIter, DeriveActiveEnum)]
#[sea_orm(rs_type = "String", db_type = "String(StringLen::N(16))")]
pub enum MessageStatus {
    #[sea_orm(string_value = "NORMAL")]
    Normal,
    #[sea_orm(string_value = "DELETED")]
    Deleted,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, EnumIter, DeriveActiveEnum)]
#[sea_orm(rs_type = "String", db_type = "String(StringLen::N(32))")]
pub enum OutboxEventType {
    #[sea_orm(string_value = "STORAGE_WARNING")]
    StorageWarning,
    #[sea_orm(string_value = "FILE_UPLOADED")]
    FileUploaded,
    #[sea_orm(string_value = "MESSAGE_CREATED")]
    MessageCreated,
    #[sea_orm(string_value = "ROUND_CONFIRMED")]
    RoundConfirmed,
    #[sea_orm(string_value = "ROUND_REJECTED")]
    RoundRejected,
    #[sea_orm(string_value = "ROUND_CANCELLED")]
    RoundCancelled,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, EnumIter, DeriveActiveEnum)]
#[sea_orm(rs_type = "String", db_type = "String(StringLen::N(16))")]
pub enum OutboxStatus {
    #[sea_orm(string_value = "PENDING")]
    Pending,
    #[sea_orm(string_value = "SENDING")]
    Sending,
    #[sea_orm(string_value = "SENT")]
    Sent,
    #[sea_orm(string_value = "FAILED")]
    Failed,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, EnumIter, DeriveActiveEnum)]
#[sea_orm(rs_type = "String", db_type = "String(StringLen::N(16))")]
pub enum PermissionType {
    #[sea_orm(string_value = "MENU")]
    Menu,
    #[sea_orm(string_value = "ACTION")]
    Action,
}
