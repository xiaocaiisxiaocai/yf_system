//! SeaORM 实体：表结构的 Code-First 事实源。
//! 任何结构变更必须同步在 migration/ 新增迁移，禁止手工改库。
pub mod enums;

pub mod audit_logs;
pub mod departments;
pub mod email_outbox;
pub mod files;
pub mod message_reads;
pub mod messages;
pub mod permissions;
pub mod project_activities;
pub mod project_members;
pub mod project_status_logs;
pub mod projects;
pub mod refresh_tokens;
pub mod role_permissions;
pub mod roles;
pub mod suppliers;
pub mod system_configs;
pub mod upload_sessions;
pub mod user_roles;
pub mod users;
