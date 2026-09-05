//! 系统参数 + 存储容量
use sea_orm::{ActiveModelTrait, DatabaseConnection, EntityTrait, Set, TransactionTrait};
use serde::Deserialize;
use serde_json::{json, Value};
use sysinfo::Disks;

use crate::entity::system_configs;
use crate::error::{ApiResult, AppError};
use crate::middleware::auth::CurrentUser;
use crate::state::AppState;

use super::audit;

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ConfigUpdate {
    pub key: String,
    pub value: String,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ConfigBatch {
    pub items: Vec<ConfigUpdate>,
}

fn validated_value(key: &str, value: &str) -> ApiResult<String> {
    let value = value.trim();
    match key {
        "upload.max_file_size" => {
            let parsed: u64 = value
                .parse()
                .map_err(|_| AppError::BadRequest("文件上限必须是整数".into()))?;
            if !(1024 * 1024..=20 * 1024 * 1024 * 1024).contains(&parsed) {
                return Err(AppError::BadRequest("文件上限必须在 1MB~20GB 之间".into()));
            }
            Ok(parsed.to_string())
        }
        "upload.chunk_size" => {
            let parsed: u64 = value
                .parse()
                .map_err(|_| AppError::BadRequest("分片大小必须是整数".into()))?;
            if !(256 * 1024..=64 * 1024 * 1024).contains(&parsed) {
                return Err(AppError::BadRequest(
                    "分片大小必须在 256KB~64MB 之间".into(),
                ));
            }
            Ok(parsed.to_string())
        }
        "upload.allowed_exts" => {
            let mut exts = Vec::new();
            for ext in value.split(',').map(str::trim).filter(|v| !v.is_empty()) {
                if ext.len() > 16 || !ext.chars().all(|c| c.is_ascii_alphanumeric()) {
                    return Err(AppError::BadRequest(format!("非法文件扩展名: {ext}")));
                }
                exts.push(ext.to_ascii_lowercase());
            }
            exts.sort();
            exts.dedup();
            if exts.is_empty() {
                return Err(AppError::BadRequest("扩展名白名单不可为空".into()));
            }
            Ok(exts.join(","))
        }
        "notify.enabled" => match value.to_ascii_lowercase().as_str() {
            "true" => Ok("true".to_string()),
            "false" => Ok("false".to_string()),
            _ => Err(AppError::BadRequest("通知开关只能是 true 或 false".into())),
        },
        "storage.warn_percent" => {
            let parsed: u8 = value
                .parse()
                .map_err(|_| AppError::BadRequest("告警阈值必须是整数".into()))?;
            if !(1..=99).contains(&parsed) {
                return Err(AppError::BadRequest("告警阈值必须在 1~99 之间".into()));
            }
            Ok(parsed.to_string())
        }
        _ => Ok(value.to_string()),
    }
}

pub async fn list(db: &DatabaseConnection) -> ApiResult<Value> {
    let all = system_configs::Entity::find().all(db).await?;
    Ok(json!(all
        .iter()
        .map(|c| json!({
            "key": c.cfg_key, "value": c.cfg_value, "description": c.description, "updatedAt": c.updated_at,
        }))
        .collect::<Vec<_>>()))
}

pub async fn update(db: &DatabaseConnection, me: &CurrentUser, req: &ConfigBatch) -> ApiResult<()> {
    if req.items.is_empty() || req.items.len() > 100 {
        return Err(AppError::BadRequest("参数数量需为 1~100".into()));
    }
    let mut seen = std::collections::HashSet::new();
    let mut validated = Vec::with_capacity(req.items.len());
    for item in &req.items {
        if !seen.insert(item.key.clone()) {
            return Err(AppError::BadRequest(format!("参数重复: {}", item.key)));
        }
        validated.push((item.key.clone(), validated_value(&item.key, &item.value)?));
    }
    let txn = db.begin().await?;
    for (key, value) in &validated {
        let Some(c) = system_configs::Entity::find_by_id(key.clone())
            .one(&txn)
            .await?
        else {
            return Err(AppError::BadRequest(format!("参数不存在: {key}")));
        };
        let mut am: system_configs::ActiveModel = c.into();
        am.cfg_value = Set(Some(value.clone()));
        am.updated_at = Set(chrono::Utc::now());
        am.update(&txn).await?;
    }
    audit::insert(
        &txn,
        Some(me.id),
        Some(me.username.clone()),
        "CONFIG_UPDATE",
        Some("system_config"),
        None,
        Some(json!({"keys": validated.iter().map(|i| &i.0).collect::<Vec<_>>()})),
        None,
    )
    .await?;
    txn.commit().await?;
    Ok(())
}

pub struct StorageSnapshot {
    pub root: String,
    pub mount_point: String,
    pub total_bytes: u64,
    pub available_bytes: u64,
    pub used_bytes: u64,
    pub used_percent: f64,
    pub warn_percent: f64,
    pub warning: bool,
}

/// 存储卷容量快照，供管理页面和后台告警复用同一套阈值计算。
pub async fn storage_snapshot(state: &AppState) -> ApiResult<StorageSnapshot> {
    let root = std::path::PathBuf::from(&state.cfg.storage.root);
    std::fs::create_dir_all(&root).ok();
    let disks = Disks::new_with_refreshed_list();
    // 找最长前缀匹配的挂载点
    let mut best: Option<&sysinfo::Disk> = None;
    for d in disks.list() {
        if root.starts_with(d.mount_point()) {
            let better = best
                .map(|b| d.mount_point().as_os_str().len() > b.mount_point().as_os_str().len())
                .unwrap_or(true);
            if better {
                best = Some(d);
            }
        }
    }
    let Some(disk) = best else {
        return Err(AppError::Internal("无法获取磁盘信息".into()));
    };
    let total = disk.total_space();
    let available = disk.available_space();
    let used_bytes = total.saturating_sub(available);
    let used_percent = if total == 0 {
        0.0
    } else {
        used_bytes as f64 * 100.0 / total as f64
    };
    let warn_percent = system_configs::Entity::find_by_id("storage.warn_percent".to_string())
        .one(&state.db)
        .await?
        .and_then(|c| c.cfg_value)
        .and_then(|v| v.parse::<f64>().ok())
        .unwrap_or(85.0);
    Ok(StorageSnapshot {
        root: state.cfg.storage.root.clone(),
        mount_point: disk.mount_point().to_string_lossy().into_owned(),
        total_bytes: total,
        available_bytes: available,
        used_bytes,
        used_percent,
        warn_percent,
        warning: used_percent >= warn_percent,
    })
}

/// 存储卷容量：返回存储根所在盘符的总量/可用量。
pub async fn storage_status(state: &AppState) -> ApiResult<Value> {
    let s = storage_snapshot(state).await?;
    Ok(json!({
        "root": s.root,
        "mountPoint": s.mount_point,
        "totalBytes": s.total_bytes,
        "availableBytes": s.available_bytes,
        "usedBytes": s.used_bytes,
        "usedPercent": s.used_percent,
        "warnPercent": s.warn_percent,
        "warning": s.warning,
    }))
}

#[cfg(test)]
mod tests {
    use super::validated_value;

    #[test]
    fn config_validation_rejects_unsafe_values() {
        assert!(validated_value("upload.chunk_size", "0").is_err());
        assert!(validated_value("storage.warn_percent", "100").is_err());
        assert!(validated_value("upload.allowed_exts", "xlsx,../exe").is_err());
        assert_eq!(validated_value("notify.enabled", "TRUE").unwrap(), "true");
    }
}
