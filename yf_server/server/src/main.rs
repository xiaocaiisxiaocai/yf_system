mod config;
mod dto;
mod entity;
mod error;
mod handler;
mod middleware;
mod notify;
mod service;
mod state;
mod storage;
mod util;

#[cfg(test)]
mod regression;
#[cfg(test)]
mod regression_business_uniqueness;
#[cfg(test)]
mod regression_delete_accounts;
#[cfg(test)]
mod regression_delete_project;
#[cfg(test)]
mod regression_identity_review;
#[cfg(test)]
mod regression_org_migration;
#[cfg(test)]
mod regression_profile;
#[cfg(test)]
mod regression_project_activity;
#[cfg(test)]
mod regression_project_members;
#[cfg(test)]
mod regression_project_workflow_migration;
#[cfg(test)]
mod regression_write_permissions;

use migration::MigratorTrait;
use state::AppState;
use std::net::SocketAddr;

#[tokio::main]
async fn main() -> Result<(), Box<dyn std::error::Error>> {
    tracing_subscriber::fmt()
        .with_env_filter(
            tracing_subscriber::EnvFilter::try_from_default_env()
                .unwrap_or_else(|_| "info,sea_orm=warn,sqlx=warn".into()),
        )
        .init();

    let cfg = config::Config::load()?;
    let addr: SocketAddr = cfg.server.addr.parse()?;

    let db = sea_orm::Database::connect(&cfg.database.url).await?;
    if cfg.database.auto_migrate {
        migration::Migrator::up(&db, None).await?;
        tracing::info!("数据库迁移完成（含种子数据）");
    }
    service::migration_cleanup::drain(&db, &cfg.storage.root).await?;

    let state = AppState::new(db, cfg);
    tokio::spawn(notify::worker::run(state.clone()));

    let app = handler::router(state);
    let listener = tokio::net::TcpListener::bind(addr).await?;
    tracing::info!("HTTP 服务监听于 http://{addr}");
    axum::serve(
        listener,
        app.into_make_service_with_connect_info::<SocketAddr>(),
    )
    .with_graceful_shutdown(async {
        let _ = tokio::signal::ctrl_c().await;
        tracing::info!("收到退出信号，正在关闭");
    })
    .await?;
    Ok(())
}
