//! 本地磁盘存储布局。
//! 约束：tmp 分片目录与 files 正式目录必须位于同一磁盘卷（原子 rename 前提）。
use std::path::{Path, PathBuf};

/// 分片临时目录：{root}/tmp/{session_id}/
pub fn tmp_dir(root: &str, session_id: &str) -> PathBuf {
    Path::new(root).join("tmp").join(session_id)
}

/// 单个分片：{root}/tmp/{session_id}/{index}.part
pub fn chunk_path(root: &str, session_id: &str, index: u32) -> PathBuf {
    tmp_dir(root, session_id).join(format!("{index}.part"))
}

/// 正式文件路径：{root}/files/{YYYY}/{MM}/{stored_name}
pub fn final_path(root: &str, now: &chrono::DateTime<chrono::Utc>, stored_name: &str) -> PathBuf {
    Path::new(root)
        .join("files")
        .join(now.format("%Y").to_string())
        .join(now.format("%m").to_string())
        .join(stored_name)
}

fn normalize_lexically(path: &Path) -> Option<PathBuf> {
    let mut normalized = PathBuf::new();
    for component in path.components() {
        match component {
            std::path::Component::CurDir => {}
            std::path::Component::ParentDir => {
                if !normalized.pop() {
                    return None;
                }
            }
            _ => normalized.push(component.as_os_str()),
        }
    }
    Some(normalized)
}

/// 防路径遍历：规范化 `.` / `..` 后仍须位于 root 之内。
///
/// 该检查不访问文件系统，可用于上传期间尚未创建的目标路径。读取已有文件时还应使用
/// [`canonical_existing_path`]，以防目录链接指向存储根之外。
pub fn ensure_within_root(root: &str, path: &Path) -> Result<(), crate::error::AppError> {
    let root = std::path::absolute(root)
        .ok()
        .and_then(|path| normalize_lexically(&path));
    let path = std::path::absolute(path)
        .ok()
        .and_then(|path| normalize_lexically(&path));
    if matches!((root, path), (Some(root), Some(path)) if path.starts_with(&root)) {
        Ok(())
    } else {
        Err(crate::error::AppError::Internal("非法存储路径".into()))
    }
}

/// 返回经文件系统解析、且确认位于存储根内的已有文件路径。
pub async fn canonical_existing_path(
    root: &str,
    path: &Path,
) -> Result<PathBuf, crate::error::AppError> {
    ensure_within_root(root, path)?;
    let canonical_root = tokio::fs::canonicalize(root)
        .await
        .map_err(|e| crate::error::AppError::Internal(format!("解析存储根目录失败: {e}")))?;
    let canonical_path = tokio::fs::canonicalize(path).await.map_err(|e| {
        if e.kind() == std::io::ErrorKind::NotFound {
            crate::error::AppError::NotFound
        } else {
            crate::error::AppError::Internal(format!("解析存储文件失败: {e}"))
        }
    })?;
    let canonical_root = canonical_root
        .to_str()
        .ok_or_else(|| crate::error::AppError::Internal("非法存储路径".into()))?;
    ensure_within_root(canonical_root, &canonical_path)?;
    Ok(canonical_path)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn rejects_parent_path_that_lexically_escapes_root() {
        let root = std::env::temp_dir().join("yf_storage_boundary_root");
        let escaped = root.join("..").join("outside.txt");
        assert!(ensure_within_root(root.to_str().unwrap(), &escaped).is_err());
    }

    #[test]
    fn relative_dot_root_does_not_accept_an_absolute_outside_path() {
        let outside = std::env::temp_dir().join("outside.txt");
        assert!(ensure_within_root(".", &outside).is_err());
    }

    #[tokio::test]
    async fn canonical_existing_path_returns_only_real_paths_inside_root() {
        let test_id = uuid::Uuid::new_v4().simple().to_string();
        let base = std::env::temp_dir().join(format!("yf_storage_boundary_{test_id}"));
        let root = base.join("root");
        let outside = base.join("outside.txt");
        let inside = root.join("inside.txt");
        std::fs::create_dir_all(&root).unwrap();
        std::fs::write(&inside, b"inside").unwrap();
        std::fs::write(&outside, b"outside").unwrap();

        assert_eq!(
            canonical_existing_path(root.to_str().unwrap(), &inside)
                .await
                .unwrap(),
            std::fs::canonicalize(&inside).unwrap()
        );
        assert!(canonical_existing_path(root.to_str().unwrap(), &outside)
            .await
            .is_err());

        std::fs::remove_dir_all(base).unwrap();
    }
}
