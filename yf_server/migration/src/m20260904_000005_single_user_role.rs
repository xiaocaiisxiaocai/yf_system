use sea_orm_migration::prelude::*;

pub struct Migration;

impl MigrationName for Migration {
    fn name(&self) -> &str {
        "m20260904_000005_single_user_role"
    }
}

#[async_trait::async_trait]
impl MigrationTrait for Migration {
    async fn up(&self, manager: &SchemaManager) -> Result<(), DbErr> {
        manager
            .create_index(
                Index::create()
                    .name("uk_user_roles_user_id")
                    .table(UserRoles::Table)
                    .col(UserRoles::UserId)
                    .unique()
                    .to_owned(),
            )
            .await
    }

    async fn down(&self, manager: &SchemaManager) -> Result<(), DbErr> {
        manager
            .drop_index(
                Index::drop()
                    .name("uk_user_roles_user_id")
                    .table(UserRoles::Table)
                    .to_owned(),
            )
            .await
    }
}

#[derive(DeriveIden)]
enum UserRoles {
    Table,
    UserId,
}
