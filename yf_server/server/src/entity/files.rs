use super::enums::{FileDirection, FileStatus};
use sea_orm::entity::prelude::*;

#[derive(Clone, Debug, PartialEq, Eq, DeriveEntityModel)]
#[sea_orm(table_name = "files")]
pub struct Model {
    #[sea_orm(primary_key)]
    pub id: u64,
    pub project_id: u64,
    pub round_id: u64,
    pub uploader_id: u64,
    /// 由上传人身份自动推导：内部=C2S，供应商=S2C
    pub direction: FileDirection,
    pub original_name: String,
    /// UUID 存储名，磁盘上不暴露原始文件名
    pub stored_name: String,
    pub ext: String,
    pub size_bytes: u64,
    pub mime_type: Option<String>,
    pub sha256: Option<String>,
    /// 相对存储根目录的路径
    pub storage_path: String,
    pub status: FileStatus,
    /// 软删除发生时间；物理清理必须从此时间计算保留期
    pub deleted_at: Option<DateTimeUtc>,
    pub created_at: DateTimeUtc,
}

#[derive(Copy, Clone, Debug, EnumIter, DeriveRelation)]
pub enum Relation {}

impl ActiveModelBehavior for ActiveModel {}
