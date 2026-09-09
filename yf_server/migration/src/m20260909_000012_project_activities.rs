//! 独立项目业务时间线及基于可证实业务记录的历史回填。
use sea_orm_migration::prelude::*;
use sea_orm_migration::sea_orm::{ConnectionTrait, DbBackend, Statement};

pub struct Migration;

impl MigrationName for Migration {
    fn name(&self) -> &str {
        "m20260909_000012_project_activities"
    }
}

async fn execute(db: &impl ConnectionTrait, sql: &str) -> Result<(), DbErr> {
    db.execute(Statement::from_string(DbBackend::MySql, sql.to_owned()))
        .await?;
    Ok(())
}

#[async_trait::async_trait]
impl MigrationTrait for Migration {
    async fn up(&self, manager: &SchemaManager) -> Result<(), DbErr> {
        let db = manager.get_connection();
        // MySQL 5.7 不支持 CREATE INDEX IF NOT EXISTS；索引随幂等建表一次完成。
        execute(
            db,
            r#"CREATE TABLE IF NOT EXISTS project_activities (
                id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
                project_id BIGINT UNSIGNED NOT NULL,
                activity_type VARCHAR(16) NOT NULL,
                action VARCHAR(32) NOT NULL,
                actor_id BIGINT UNSIGNED NULL,
                actor_name VARCHAR(128) NOT NULL,
                occurred_at DATETIME(3) NOT NULL,
                title VARCHAR(160) NOT NULL,
                summary VARCHAR(160) NULL,
                round_id BIGINT UNSIGNED NULL,
                round_no INT NULL,
                target_id BIGINT UNSIGNED NULL,
                source_key VARCHAR(191) NOT NULL,
                PRIMARY KEY (id),
                UNIQUE KEY uk_project_activities_source (source_key),
                KEY idx_project_activities_project_time (project_id, occurred_at, id),
                KEY idx_project_activities_project_type_time (project_id, activity_type, occurred_at, id),
                CONSTRAINT fk_project_activities_project FOREIGN KEY (project_id)
                    REFERENCES projects (id) ON DELETE RESTRICT
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4"#,
        )
        .await?;

        // 项目创建时间是明确业务事实；不从 updated_at 推测开始或终止时间。
        execute(
            db,
            r#"INSERT IGNORE INTO project_activities
                (project_id, activity_type, action, actor_id, actor_name, occurred_at,
                 title, summary, target_id, source_key)
            SELECT p.id, 'PROJECT', 'CREATE', p.created_by,
                   COALESCE(NULLIF(u.real_name, ''), NULLIF(u.employee_no, ''), '未知'),
                   p.created_at, '创建项目',
                   CASE WHEN JSON_VALID(a.detail)
                             AND JSON_TYPE(JSON_EXTRACT(a.detail, '$.name')) = 'STRING'
                        THEN LEFT(JSON_UNQUOTE(JSON_EXTRACT(a.detail, '$.name')), 160)
                        ELSE NULL END,
                   p.id,
                   CONCAT('project:', p.id, ':create')
              FROM projects p
              LEFT JOIN users u ON u.id = p.created_by
              LEFT JOIN (
                    SELECT al.* FROM audit_logs al
                    INNER JOIN (
                        SELECT target_id, MIN(id) AS id
                          FROM audit_logs
                         WHERE action = 'PROJECT_CREATE' AND target_type = 'project'
                           AND target_id REGEXP '^[0-9]+$'
                         GROUP BY target_id
                    ) first_log ON first_log.id = al.id
              ) a ON a.target_id = CAST(p.id AS CHAR)"#,
        )
        .await?;

        // 轮次状态日志是创建及所有状态转换的事实源。
        execute(
            db,
            r#"INSERT IGNORE INTO project_activities
                (project_id, activity_type, action, actor_id, actor_name, occurred_at,
                 title, summary, round_id, round_no, target_id, source_key)
            SELECT r.project_id, 'ROUND',
                   CASE l.to_status
                       WHEN 'PENDING' THEN 'CREATE'
                       WHEN 'CONFIRMED' THEN 'CONFIRM'
                       WHEN 'REJECTED' THEN 'REJECT'
                       WHEN 'CANCELLED' THEN 'CANCEL'
                   END,
                   l.operator_id,
                   COALESCE(NULLIF(u.real_name, ''), NULLIF(u.employee_no, ''), '未知'),
                   l.created_at,
                   CASE l.to_status
                       WHEN 'PENDING' THEN CONCAT('创建第', r.round_no, '轮')
                       WHEN 'CONFIRMED' THEN CONCAT('确认第', r.round_no, '轮')
                       WHEN 'REJECTED' THEN CONCAT('驳回第', r.round_no, '轮')
                       WHEN 'CANCELLED' THEN CONCAT('撤销第', r.round_no, '轮')
                   END,
                   LEFT(CASE WHEN l.to_status = 'REJECTED' THEN l.reason
                             ELSE COALESCE(NULLIF(r.title, ''), NULLIF(r.remark, '')) END, 160),
                   r.id, r.round_no, r.id,
                   CASE WHEN l.to_status = 'PENDING'
                        THEN CONCAT('round:', r.id, ':create')
                        ELSE CONCAT('round-log:', l.id) END
              FROM round_status_logs l
              INNER JOIN rounds r ON r.id = l.round_id
              LEFT JOIN users u ON u.id = l.operator_id
             WHERE l.to_status IN ('PENDING', 'CONFIRMED', 'REJECTED', 'CANCELLED')"#,
        )
        .await?;

        // 旧数据若缺失“创建”状态日志，rounds.created_by/created_at 仍是明确的创建事实。
        execute(
            db,
            r#"INSERT IGNORE INTO project_activities
                (project_id, activity_type, action, actor_id, actor_name, occurred_at,
                 title, summary, round_id, round_no, target_id, source_key)
            SELECT r.project_id, 'ROUND', 'CREATE', r.created_by,
                   COALESCE(NULLIF(u.real_name, ''), NULLIF(u.employee_no, ''), '未知'),
                   r.created_at, CONCAT('创建第', r.round_no, '轮'),
                   LEFT(COALESCE(NULLIF(r.title, ''), NULLIF(r.remark, '')), 160),
                   r.id, r.round_no, r.id, CONCAT('round:', r.id, ':create')
              FROM rounds r
              LEFT JOIN users u ON u.id = r.created_by
             WHERE NOT EXISTS (
                   SELECT 1 FROM round_status_logs l
                    WHERE l.round_id = r.id AND l.to_status = 'PENDING'
             )"#,
        )
        .await?;

        execute(
            db,
            r#"INSERT IGNORE INTO project_activities
                (project_id, activity_type, action, actor_id, actor_name, occurred_at,
                 title, summary, round_id, round_no, target_id, source_key)
            SELECT f.project_id, 'FILE', 'UPLOAD', f.uploader_id,
                   COALESCE(NULLIF(u.real_name, ''), NULLIF(u.employee_no, ''), '未知'),
                   f.created_at, '上传文件', LEFT(f.original_name, 160),
                   f.round_id, r.round_no, f.id, CONCAT('file:', f.id, ':upload')
              FROM files f
              INNER JOIN rounds r ON r.id = f.round_id AND r.project_id = f.project_id
              LEFT JOIN users u ON u.id = f.uploader_id"#,
        )
        .await?;

        // files 没有 deleted_by；仅在严格字符串 ID 匹配到明确审计时恢复操作者，否则为“未知”。
        execute(
            db,
            r#"INSERT IGNORE INTO project_activities
                (project_id, activity_type, action, actor_id, actor_name, occurred_at,
                 title, summary, round_id, round_no, target_id, source_key)
            SELECT f.project_id, 'FILE', 'DELETE', a.user_id,
                   COALESCE(NULLIF(u.real_name, ''), NULLIF(a.employee_no, ''), '未知'),
                   f.deleted_at, '删除文件', LEFT(f.original_name, 160),
                   f.round_id, r.round_no, f.id, CONCAT('file:', f.id, ':delete')
              FROM files f
              INNER JOIN rounds r ON r.id = f.round_id AND r.project_id = f.project_id
              LEFT JOIN (
                    SELECT al.* FROM audit_logs al
                    INNER JOIN (
                        SELECT target_id, MAX(id) AS id
                          FROM audit_logs
                         WHERE action = 'FILE_DELETE' AND target_type = 'file'
                           AND target_id REGEXP '^[0-9]+$'
                         GROUP BY target_id
                    ) latest ON latest.id = al.id
              ) a ON a.target_id = CAST(f.id AS CHAR)
              LEFT JOIN users u ON u.id = a.user_id
             WHERE f.status = 'DELETED' AND f.deleted_at IS NOT NULL"#,
        )
        .await?;

        execute(
            db,
            r#"INSERT IGNORE INTO project_activities
                (project_id, activity_type, action, actor_id, actor_name, occurred_at,
                 title, summary, round_id, round_no, target_id, source_key)
            SELECT m.project_id, 'MESSAGE', 'CREATE', m.sender_id,
                   COALESCE(NULLIF(u.real_name, ''), NULLIF(u.employee_no, ''), '未知'),
                   m.created_at, '发表留言',
                   CASE WHEN m.status = 'DELETED' THEN NULL ELSE LEFT(m.content, 160) END,
                   m.round_id, r.round_no, m.id, CONCAT('message:', m.id, ':create')
              FROM messages m
              LEFT JOIN rounds r ON r.id = m.round_id AND r.project_id = m.project_id
              LEFT JOIN users u ON u.id = m.sender_id"#,
        )
        .await?;

        execute(
            db,
            r#"INSERT IGNORE INTO project_activities
                (project_id, activity_type, action, actor_id, actor_name, occurred_at,
                 title, summary, round_id, round_no, target_id, source_key)
            SELECT m.project_id, 'MESSAGE', 'DELETE', m.deleted_by,
                   COALESCE(NULLIF(u.real_name, ''), NULLIF(u.employee_no, ''), '未知'),
                   m.deleted_at, '删除留言', NULL,
                   m.round_id, r.round_no, m.id, CONCAT('message:', m.id, ':delete')
              FROM messages m
              LEFT JOIN rounds r ON r.id = m.round_id AND r.project_id = m.project_id
              LEFT JOIN users u ON u.id = m.deleted_by
             WHERE m.status = 'DELETED' AND m.deleted_at IS NOT NULL"#,
        )
        .await?;

        // 仅项目状态、编辑和成员变动缺少独立业务历史，且 target_id 必须与真实项目 ID 完全一致。
        execute(
            db,
            r#"INSERT IGNORE INTO project_activities
                (project_id, activity_type, action, actor_id, actor_name, occurred_at,
                 title, summary, target_id, source_key)
            SELECT p.id, 'PROJECT',
                   CASE
                       WHEN a.action = 'PROJECT_UPDATE' THEN 'UPDATE'
                       WHEN a.action = 'PROJECT_MEMBERS' THEN 'MEMBERS_CHANGE'
                       WHEN JSON_UNQUOTE(JSON_EXTRACT(a.detail, '$.to')) = 'IN_PROGRESS' THEN 'START'
                       WHEN JSON_UNQUOTE(JSON_EXTRACT(a.detail, '$.to')) = 'COMPLETED' THEN 'COMPLETE'
                       WHEN JSON_UNQUOTE(JSON_EXTRACT(a.detail, '$.to')) = 'TERMINATED' THEN 'TERMINATE'
                   END,
                   a.user_id,
                   COALESCE(NULLIF(u.real_name, ''), NULLIF(a.employee_no, ''), '未知'),
                   a.created_at,
                   CASE
                       WHEN a.action = 'PROJECT_UPDATE' THEN '编辑项目'
                       WHEN a.action = 'PROJECT_MEMBERS' THEN '调整项目成员'
                       WHEN JSON_UNQUOTE(JSON_EXTRACT(a.detail, '$.to')) = 'IN_PROGRESS' THEN '开始项目'
                       WHEN JSON_UNQUOTE(JSON_EXTRACT(a.detail, '$.to')) = 'COMPLETED' THEN '完成项目'
                       WHEN JSON_UNQUOTE(JSON_EXTRACT(a.detail, '$.to')) = 'TERMINATED' THEN '终止项目'
                   END,
                   NULL, p.id, CONCAT('audit:', a.id)
              FROM audit_logs a
              INNER JOIN projects p ON a.target_id = CAST(p.id AS CHAR)
              LEFT JOIN users u ON u.id = a.user_id
             WHERE a.target_type = 'project'
               AND a.target_id REGEXP '^[0-9]+$'
               AND (
                    a.action IN ('PROJECT_UPDATE', 'PROJECT_MEMBERS')
                    OR (a.action = 'PROJECT_STATUS' AND JSON_VALID(a.detail)
                        AND JSON_UNQUOTE(JSON_EXTRACT(a.detail, '$.to'))
                            IN ('IN_PROGRESS', 'COMPLETED', 'TERMINATED'))
               )"#,
        )
        .await?;

        Ok(())
    }

    async fn down(&self, manager: &SchemaManager) -> Result<(), DbErr> {
        manager
            .drop_table(
                Table::drop()
                    .if_exists()
                    .table(ProjectActivities::Table)
                    .to_owned(),
            )
            .await
    }
}

#[derive(DeriveIden)]
enum ProjectActivities {
    Table,
}
