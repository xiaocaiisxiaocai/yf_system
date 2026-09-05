use super::enums::UploadStatus;
use sea_orm::entity::prelude::*;

/// 分片上传会话。已传分片清单不落库，断点续传时扫描 temp_dir 获得
#[derive(Clone, Debug, PartialEq, Eq, DeriveEntityModel)]
#[sea_orm(table_name = "upload_sessions")]
pub struct Model {
    #[sea_orm(
        primary_key,
        auto_increment = false,
        column_type = "String(StringLen::N(36))"
    )]
    pub id: String,
    pub project_id: u64,
    pub round_id: u64,
    pub uploader_id: u64,
    pub file_name: String,
    pub file_size: u64,
    /// 客户端整体 MD5（可选，校验/秒传扩展用）
    pub file_md5: Option<String>,
    pub chunk_size: u32,
    pub total_chunks: u32,
    pub temp_dir: String,
    pub status: UploadStatus,
    /// 合并成功后的文件记录，用于 completed 状态精确幂等返回
    pub result_file_id: Option<u64>,
    /// 超时由清理任务标记 EXPIRED 并删除临时目录
    pub expires_at: DateTimeUtc,
    pub created_at: DateTimeUtc,
    pub updated_at: DateTimeUtc,
}

#[derive(Copy, Clone, Debug, EnumIter, DeriveRelation)]
pub enum Relation {}

impl ActiveModelBehavior for ActiveModel {}
