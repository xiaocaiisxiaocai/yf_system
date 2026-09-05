pub use sea_orm_migration::prelude::*;

mod m20260903_000001_init;
mod m20260903_000002_seed;
mod m20260904_000003_integrity_hardening;
mod m20260904_000004_storage_warning;
mod m20260904_000005_single_user_role;

pub struct Migrator;

#[async_trait::async_trait]
impl MigratorTrait for Migrator {
    fn migrations() -> Vec<Box<dyn MigrationTrait>> {
        vec![
            Box::new(m20260903_000001_init::Migration),
            Box::new(m20260903_000002_seed::Migration),
            Box::new(m20260904_000003_integrity_hardening::Migration),
            Box::new(m20260904_000004_storage_warning::Migration),
            Box::new(m20260904_000005_single_user_role::Migration),
        ]
    }
}
