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

/// 防路径遍历：拼接后必须仍位于 root 之内
pub fn ensure_within_root(root: &str, path: &Path) -> Result<(), crate::error::AppError> {
    let root = Path::new(root);
    if path.starts_with(root) {
        Ok(())
    } else {
        Err(crate::error::AppError::Internal("非法存储路径".into()))
    }
}
