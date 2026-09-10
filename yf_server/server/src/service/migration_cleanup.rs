//! Cleanup for physical project files captured by the one-way project workflow migration.
//!
//! Migration code cannot safely know the configured storage root. It records the paths in a
//! short-lived queue, and startup drains that queue after migrations have completed. A path that
//! cannot be proven to be inside the configured root stops startup and remains queued for repair.

use sea_orm::{ConnectionTrait, DatabaseConnection, DbBackend, DbErr, Statement};
use std::path::{Component, Path, PathBuf};

#[derive(Debug)]
struct CleanupPath {
    id: u64,
    kind: String,
    value: String,
}

fn db_error(message: impl Into<String>) -> DbErr {
    DbErr::Custom(message.into())
}

async fn queue_exists(db: &DatabaseConnection) -> Result<bool, DbErr> {
    let row = db
        .query_one(Statement::from_string(
            DbBackend::MySql,
            "SELECT COUNT(*) AS n FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = 'project_workflow_cleanup_paths'".to_owned(),
        ))
        .await?;
    Ok(row
        .map(|row| row.try_get::<i64>("", "n"))
        .transpose()?
        .unwrap_or_default()
        > 0)
}

async fn load_paths(db: &DatabaseConnection) -> Result<Vec<CleanupPath>, DbErr> {
    let rows = db
        .query_all(Statement::from_string(
            DbBackend::MySql,
            "SELECT id, path_kind, path_value FROM project_workflow_cleanup_paths ORDER BY id"
                .to_owned(),
        ))
        .await?;
    rows.into_iter()
        .map(|row| {
            Ok(CleanupPath {
                id: row.try_get("", "id")?,
                kind: row.try_get("", "path_kind")?,
                value: row.try_get("", "path_value")?,
            })
        })
        .collect()
}

fn lexical_path(root: &Path, raw: &Path) -> Result<PathBuf, DbErr> {
    if raw.as_os_str().is_empty() {
        return Err(db_error("迁移清理队列包含空路径"));
    }
    let candidate = if raw.is_absolute() {
        raw.to_path_buf()
    } else if raw.has_root() {
        return Err(db_error(format!(
            "迁移清理路径使用了未限定盘符的根路径: {}",
            raw.display()
        )));
    } else {
        root.join(raw)
    };

    // Reject traversal before touching the filesystem. The existing-ancestor canonicalization
    // below additionally catches symlinked directories which point outside the storage root.
    if candidate
        .components()
        .any(|component| matches!(component, Component::ParentDir))
    {
        return Err(db_error(format!(
            "迁移清理路径包含父级遍历: {}",
            raw.display()
        )));
    }
    // Relative paths can be checked against the canonical root immediately. Absolute values are
    // intentionally checked after canonicalizing their existing ancestor: on Windows
    // `canonicalize` uses the `\\?\` namespace while a value persisted before startup usually
    // uses the ordinary `D:\...` namespace.
    if !raw.is_absolute() && (!candidate.starts_with(root) || candidate == root) {
        return Err(db_error(format!(
            "迁移清理路径位于存储根目录之外: {}",
            raw.display()
        )));
    }
    Ok(candidate)
}

async fn resolve_path(root: &Path, raw: &Path) -> Result<PathBuf, DbErr> {
    let candidate = lexical_path(root, raw)?;
    let mut probe = candidate.clone();
    let mut missing = Vec::new();

    loop {
        match tokio::fs::symlink_metadata(&probe).await {
            Ok(_) => break,
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
                let name = probe
                    .file_name()
                    .ok_or_else(|| db_error(format!("无法解析迁移清理路径: {}", raw.display())))?;
                missing.push(name.to_os_string());
                probe.pop();
            }
            Err(error) => {
                return Err(db_error(format!(
                    "读取迁移清理路径失败 {}: {error}",
                    raw.display()
                )))
            }
        }
    }

    let canonical_probe = tokio::fs::canonicalize(&probe)
        .await
        .map_err(|error| db_error(format!("解析迁移清理路径失败 {}: {error}", raw.display())))?;
    if !canonical_probe.starts_with(root) || canonical_probe == root {
        return Err(db_error(format!(
            "迁移清理路径通过符号链接越出存储根目录: {}",
            raw.display()
        )));
    }

    let mut resolved = canonical_probe;
    for component in missing.into_iter().rev() {
        resolved.push(component);
    }
    if resolved == root || !resolved.starts_with(root) {
        return Err(db_error(format!(
            "迁移清理路径解析后位于存储根目录之外: {}",
            raw.display()
        )));
    }
    Ok(resolved)
}

async fn remove_path(root: &Path, path: &CleanupPath) -> Result<(), DbErr> {
    let resolved = resolve_path(root, Path::new(&path.value)).await?;
    let result = match path.kind.as_str() {
        "FILE" => tokio::fs::remove_file(&resolved).await,
        "TEMP_DIR" => tokio::fs::remove_dir_all(&resolved).await,
        other => return Err(db_error(format!("迁移清理队列包含未知路径类型 {other}"))),
    };
    if let Err(error) = result {
        if error.kind() != std::io::ErrorKind::NotFound {
            return Err(db_error(format!(
                "删除迁移遗留路径失败 {}: {error}",
                resolved.display()
            )));
        }
    }
    Ok(())
}

async fn delete_queue_row(db: &DatabaseConnection, id: u64) -> Result<(), DbErr> {
    db.execute(Statement::from_sql_and_values(
        DbBackend::MySql,
        "DELETE FROM project_workflow_cleanup_paths WHERE id = ?",
        [id.into()],
    ))
    .await?;
    Ok(())
}

/// Drain the one-way migration queue. The queue table is dropped only after every path has been
/// handled. Any validation or filesystem failure is returned to `main`, which prevents the HTTP
/// listener from starting while preserving the queue for the next repair attempt.
pub async fn drain(db: &DatabaseConnection, configured_root: &str) -> Result<(), DbErr> {
    if !queue_exists(db).await? {
        return Ok(());
    }
    let paths = load_paths(db).await?;
    if paths.is_empty() {
        db.execute(Statement::from_string(
            DbBackend::MySql,
            "DROP TABLE IF EXISTS project_workflow_cleanup_paths".to_owned(),
        ))
        .await?;
        return Ok(());
    }

    tokio::fs::create_dir_all(configured_root)
        .await
        .map_err(|error| db_error(format!("无法创建存储根目录 {configured_root}: {error}")))?;
    let root = tokio::fs::canonicalize(configured_root)
        .await
        .map_err(|error| db_error(format!("无法解析存储根目录 {configured_root}: {error}")))?;
    if !root.is_dir() {
        return Err(db_error(format!("存储根目录不是目录: {}", root.display())));
    }

    for path in &paths {
        remove_path(&root, path).await?;
        delete_queue_row(db, path.id).await?;
    }

    db.execute(Statement::from_string(
        DbBackend::MySql,
        "DROP TABLE IF EXISTS project_workflow_cleanup_paths".to_owned(),
    ))
    .await?;
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::{lexical_path, remove_path, resolve_path, CleanupPath};
    use std::path::Path;

    #[test]
    fn rejects_parent_traversal_and_root_itself() {
        let root = Path::new(r"D:\storage");
        assert!(lexical_path(root, Path::new("../outside.txt")).is_err());
        // Absolute values are checked against the canonical root in resolve_path because Windows
        // may represent the same path as `D:\...` or `\\?\D:\...`.
        assert!(lexical_path(root, Path::new(r"D:\storage")).is_ok());
        assert!(lexical_path(root, Path::new("files/report.pdf")).is_ok());
    }

    #[tokio::test]
    async fn resolves_absolute_temp_dir_and_removes_file_and_directory() {
        let root =
            std::env::temp_dir().join(format!("yf-migration-cleanup-{}", uuid::Uuid::new_v4()));
        let file = root.join("files").join("legacy.pdf");
        let temp_dir = root.join("tmp").join("legacy-upload");
        tokio::fs::create_dir_all(file.parent().unwrap())
            .await
            .unwrap();
        tokio::fs::create_dir_all(&temp_dir).await.unwrap();
        tokio::fs::write(&file, b"legacy").await.unwrap();
        tokio::fs::write(temp_dir.join("0.part"), b"chunk")
            .await
            .unwrap();

        let canonical_root = tokio::fs::canonicalize(&root).await.unwrap();
        let resolved_file = resolve_path(&canonical_root, Path::new("files/legacy.pdf"))
            .await
            .unwrap();
        let absolute_temp = temp_dir.to_string_lossy().to_string();
        let resolved_temp = resolve_path(&canonical_root, Path::new(&absolute_temp))
            .await
            .unwrap();
        assert!(resolve_path(&canonical_root, &canonical_root)
            .await
            .is_err());
        assert!(resolved_file.ends_with(Path::new("files").join("legacy.pdf")));
        assert!(resolved_temp.ends_with(Path::new("tmp").join("legacy-upload")));

        remove_path(
            &canonical_root,
            &CleanupPath {
                id: 1,
                kind: "FILE".into(),
                value: "files/legacy.pdf".into(),
            },
        )
        .await
        .unwrap();
        remove_path(
            &canonical_root,
            &CleanupPath {
                id: 2,
                kind: "TEMP_DIR".into(),
                value: absolute_temp.clone(),
            },
        )
        .await
        .unwrap();
        assert!(!file.exists());
        assert!(!temp_dir.exists());
        // A retry after a process crash between filesystem deletion and queue-row deletion is
        // idempotent: already-missing paths are treated as successfully handled.
        remove_path(
            &canonical_root,
            &CleanupPath {
                id: 1,
                kind: "FILE".into(),
                value: "files/legacy.pdf".into(),
            },
        )
        .await
        .unwrap();
        remove_path(
            &canonical_root,
            &CleanupPath {
                id: 2,
                kind: "TEMP_DIR".into(),
                value: absolute_temp,
            },
        )
        .await
        .unwrap();

        assert!(resolve_path(&canonical_root, Path::new("../outside"))
            .await
            .is_err());
        let outside = root
            .parent()
            .unwrap()
            .join(format!("yf-migration-outside-{}", uuid::Uuid::new_v4()));
        assert!(resolve_path(&canonical_root, &outside).await.is_err());
        tokio::fs::remove_dir_all(&root).await.unwrap();
    }
}
