use migration::Migrator;

#[tokio::main]
async fn main() {
    // cli::run_cli 内部会初始化 tracing，此处不要重复设置全局 subscriber
    sea_orm_migration::cli::run_cli(Migrator).await;
}
