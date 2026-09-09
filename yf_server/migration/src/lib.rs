pub use sea_orm_migration::prelude::*;

mod m20260903_000001_init;
mod m20260903_000002_seed;
mod m20260904_000003_integrity_hardening;
mod m20260904_000004_storage_warning;
mod m20260904_000005_single_user_role;
mod m20260908_000006_identity_fields_cleanup;
mod m20260908_000007_identity_index_cleanup;
mod m20260909_000008_drop_users_phone;
mod m20260909_000009_drop_supplier_contact_fields;
mod m20260909_000010_org_structure_kinds;
mod m20260909_000011_validate_org_structure_kinds;
mod m20260909_000012_project_activities;
mod m20260909_000013_delete_permissions;

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
            Box::new(m20260908_000006_identity_fields_cleanup::Migration),
            Box::new(m20260908_000007_identity_index_cleanup::Migration),
            Box::new(m20260909_000008_drop_users_phone::Migration),
            Box::new(m20260909_000009_drop_supplier_contact_fields::Migration),
            Box::new(m20260909_000010_org_structure_kinds::Migration),
            Box::new(m20260909_000011_validate_org_structure_kinds::Migration),
            Box::new(m20260909_000012_project_activities::Migration),
            Box::new(m20260909_000013_delete_permissions::Migration),
        ]
    }
}
