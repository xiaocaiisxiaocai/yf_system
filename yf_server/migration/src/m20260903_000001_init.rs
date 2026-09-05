//! 初始迁移：创建全部 19 张表（结构定义见《02-数据库设计.md》）。
//! 目标库：MySQL 5.7，utf8mb4（库级默认，建表不重复声明）。
use sea_orm_migration::prelude::*;

pub struct Migration;

impl MigrationName for Migration {
    fn name(&self) -> &str {
        "m20260903_000001_init"
    }
}

#[async_trait::async_trait]
impl MigrationTrait for Migration {
    async fn up(&self, m: &SchemaManager) -> Result<(), DbErr> {
        create_suppliers(m).await?;
        create_departments(m).await?;
        create_users(m).await?;
        create_roles(m).await?;
        create_permissions(m).await?;
        create_role_permissions(m).await?;
        create_user_roles(m).await?;
        create_projects(m).await?;
        create_project_members(m).await?;
        create_rounds(m).await?;
        create_round_status_logs(m).await?;
        create_upload_sessions(m).await?;
        create_files(m).await?;
        create_messages(m).await?;
        create_message_reads(m).await?;
        create_email_outbox(m).await?;
        create_audit_logs(m).await?;
        create_refresh_tokens(m).await?;
        create_system_configs(m).await?;
        Ok(())
    }

    async fn down(&self, m: &SchemaManager) -> Result<(), DbErr> {
        // 逆依赖顺序
        m.drop_table(
            Table::drop()
                .if_exists()
                .table(SystemConfigs::Table)
                .to_owned(),
        )
        .await?;
        m.drop_table(
            Table::drop()
                .if_exists()
                .table(RefreshTokens::Table)
                .to_owned(),
        )
        .await?;
        m.drop_table(Table::drop().if_exists().table(AuditLogs::Table).to_owned())
            .await?;
        m.drop_table(
            Table::drop()
                .if_exists()
                .table(EmailOutbox::Table)
                .to_owned(),
        )
        .await?;
        m.drop_table(
            Table::drop()
                .if_exists()
                .table(MessageReads::Table)
                .to_owned(),
        )
        .await?;
        m.drop_table(Table::drop().if_exists().table(Messages::Table).to_owned())
            .await?;
        m.drop_table(Table::drop().if_exists().table(Files::Table).to_owned())
            .await?;
        m.drop_table(
            Table::drop()
                .if_exists()
                .table(UploadSessions::Table)
                .to_owned(),
        )
        .await?;
        m.drop_table(
            Table::drop()
                .if_exists()
                .table(RoundStatusLogs::Table)
                .to_owned(),
        )
        .await?;
        m.drop_table(Table::drop().if_exists().table(Rounds::Table).to_owned())
            .await?;
        m.drop_table(
            Table::drop()
                .if_exists()
                .table(ProjectMembers::Table)
                .to_owned(),
        )
        .await?;
        m.drop_table(Table::drop().if_exists().table(Projects::Table).to_owned())
            .await?;
        m.drop_table(Table::drop().if_exists().table(UserRoles::Table).to_owned())
            .await?;
        m.drop_table(
            Table::drop()
                .if_exists()
                .table(RolePermissions::Table)
                .to_owned(),
        )
        .await?;
        m.drop_table(
            Table::drop()
                .if_exists()
                .table(Permissions::Table)
                .to_owned(),
        )
        .await?;
        m.drop_table(Table::drop().if_exists().table(Roles::Table).to_owned())
            .await?;
        m.drop_table(Table::drop().if_exists().table(Users::Table).to_owned())
            .await?;
        m.drop_table(
            Table::drop()
                .if_exists()
                .table(Departments::Table)
                .to_owned(),
        )
        .await?;
        m.drop_table(Table::drop().if_exists().table(Suppliers::Table).to_owned())
            .await?;
        Ok(())
    }
}

// ---------- 列定义辅助 ----------

fn id_pk<T: IntoIden>(c: T) -> ColumnDef {
    ColumnDef::new(c)
        .big_unsigned()
        .not_null()
        .auto_increment()
        .primary_key()
        .to_owned()
}

fn big<T: IntoIden>(c: T) -> ColumnDef {
    ColumnDef::new(c).big_unsigned().not_null().to_owned()
}

fn big_null<T: IntoIden>(c: T) -> ColumnDef {
    ColumnDef::new(c).big_unsigned().to_owned()
}

fn string<T: IntoIden>(c: T, n: u32) -> ColumnDef {
    ColumnDef::new(c).string_len(n).not_null().to_owned()
}

fn string_null<T: IntoIden>(c: T, n: u32) -> ColumnDef {
    ColumnDef::new(c).string_len(n).to_owned()
}

fn string_default<T: IntoIden>(c: T, n: u32, d: &str) -> ColumnDef {
    ColumnDef::new(c)
        .string_len(n)
        .not_null()
        .default(d)
        .to_owned()
}

fn int<T: IntoIden>(c: T) -> ColumnDef {
    ColumnDef::new(c).integer().not_null().to_owned()
}

fn int_default<T: IntoIden>(c: T, d: i32) -> ColumnDef {
    ColumnDef::new(c).integer().not_null().default(d).to_owned()
}

fn bool_default<T: IntoIden>(c: T, d: bool) -> ColumnDef {
    ColumnDef::new(c).boolean().not_null().default(d).to_owned()
}

fn ts<T: IntoIden>(c: T) -> ColumnDef {
    ColumnDef::new(c)
        .date_time()
        .not_null()
        .default(Expr::current_timestamp())
        .to_owned()
}

fn ts_updated<T: IntoIden>(c: T) -> ColumnDef {
    let mut d = ColumnDef::new(c)
        .date_time()
        .not_null()
        .default(Expr::current_timestamp())
        .to_owned();
    d.extra("ON UPDATE CURRENT_TIMESTAMP");
    d
}

fn ts_null<T: IntoIden>(c: T) -> ColumnDef {
    ColumnDef::new(c).date_time().to_owned()
}

fn text<T: IntoIden>(c: T) -> ColumnDef {
    ColumnDef::new(c).text().not_null().to_owned()
}

fn fk(
    name: &str,
    from_t: impl IntoIden + 'static,
    from_c: impl IntoIden + 'static,
    to_t: impl IntoIden + 'static,
    to_c: impl IntoIden + 'static,
) -> ForeignKeyCreateStatement {
    ForeignKey::create()
        .name(name)
        .from(from_t, from_c)
        .to(to_t, to_c)
        .on_delete(ForeignKeyAction::Restrict)
        .to_owned()
}

// ---------- 各表 ----------

async fn create_suppliers(m: &SchemaManager<'_>) -> Result<(), DbErr> {
    m.create_table(
        Table::create()
            .table(Suppliers::Table)
            .if_not_exists()
            .col(id_pk(Suppliers::Id))
            .col(string(Suppliers::Name, 128))
            .col(string(Suppliers::Code, 64).unique_key())
            .col(string_null(Suppliers::ContactName, 64))
            .col(string_null(Suppliers::ContactPhone, 32))
            .col(string_null(Suppliers::ContactEmail, 128))
            .col(string_null(Suppliers::Address, 255))
            .col(string_null(Suppliers::Remark, 512))
            .col(string_default(Suppliers::Status, 16, "ACTIVE"))
            .col(big_null(Suppliers::CreatedBy))
            .col(ts(Suppliers::CreatedAt))
            .col(ts_updated(Suppliers::UpdatedAt))
            .to_owned(),
    )
    .await
}

async fn create_departments(m: &SchemaManager<'_>) -> Result<(), DbErr> {
    m.create_table(
        Table::create()
            .table(Departments::Table)
            .if_not_exists()
            .col(id_pk(Departments::Id))
            .col(string(Departments::Name, 64))
            .col(big_null(Departments::ParentId))
            .col(int_default(Departments::SortNo, 0))
            .col(string_default(Departments::Status, 16, "ACTIVE"))
            .col(ts(Departments::CreatedAt))
            .col(ts_updated(Departments::UpdatedAt))
            .to_owned(),
    )
    .await?;
    m.create_index(
        Index::create()
            .name("idx_departments_parent")
            .table(Departments::Table)
            .col(Departments::ParentId)
            .to_owned(),
    )
    .await
}

async fn create_users(m: &SchemaManager<'_>) -> Result<(), DbErr> {
    let mut fk_supplier = fk(
        "fk_users_supplier",
        Users::Table,
        Users::SupplierId,
        Suppliers::Table,
        Suppliers::Id,
    );
    m.create_table(
        Table::create()
            .table(Users::Table)
            .if_not_exists()
            .col(id_pk(Users::Id))
            .col(string(Users::Username, 64).unique_key())
            .col(string(Users::PasswordHash, 255))
            .col(string(Users::RealName, 64))
            .col(string(Users::Email, 128))
            .col(string_null(Users::Phone, 32))
            .col(string(Users::UserType, 16))
            .col(big_null(Users::SupplierId))
            .col(big_null(Users::DepartmentId))
            .col(string_default(Users::Status, 16, "ACTIVE"))
            .col(bool_default(Users::MustChangePassword, true))
            .col(int_default(Users::FailedLoginAttempts, 0))
            .col(ts_null(Users::LockedUntil))
            .col(ts_null(Users::LastLoginAt))
            .col(string_null(Users::LastLoginIp, 64))
            .col(big_null(Users::CreatedBy))
            .col(ts(Users::CreatedAt))
            .col(ts_updated(Users::UpdatedAt))
            .foreign_key(&mut fk_supplier)
            .to_owned(),
    )
    .await?;
    for stmt in [
        Index::create()
            .name("idx_users_supplier")
            .table(Users::Table)
            .col(Users::SupplierId)
            .to_owned(),
        Index::create()
            .name("idx_users_department")
            .table(Users::Table)
            .col(Users::DepartmentId)
            .to_owned(),
    ] {
        m.create_index(stmt).await?;
    }
    Ok(())
}

async fn create_roles(m: &SchemaManager<'_>) -> Result<(), DbErr> {
    m.create_table(
        Table::create()
            .table(Roles::Table)
            .if_not_exists()
            .col(id_pk(Roles::Id))
            .col(string(Roles::Code, 64).unique_key())
            .col(string(Roles::Name, 64))
            .col(string_null(Roles::Description, 255))
            .col(bool_default(Roles::IsBuiltIn, false))
            .col(string_default(Roles::Status, 16, "ACTIVE"))
            .col(ts(Roles::CreatedAt))
            .col(ts_updated(Roles::UpdatedAt))
            .to_owned(),
    )
    .await
}

async fn create_permissions(m: &SchemaManager<'_>) -> Result<(), DbErr> {
    m.create_table(
        Table::create()
            .table(Permissions::Table)
            .if_not_exists()
            .col(id_pk(Permissions::Id))
            .col(string(Permissions::Code, 128).unique_key())
            .col(string(Permissions::Name, 64))
            .col(string(Permissions::Type, 16))
            .col(big_null(Permissions::ParentId))
            .col(int_default(Permissions::SortNo, 0))
            .to_owned(),
    )
    .await?;
    m.create_index(
        Index::create()
            .name("idx_permissions_parent")
            .table(Permissions::Table)
            .col(Permissions::ParentId)
            .to_owned(),
    )
    .await
}

async fn create_role_permissions(m: &SchemaManager<'_>) -> Result<(), DbErr> {
    let mut pk = Index::create()
        .name("pk_role_permissions")
        .col(RolePermissions::RoleId)
        .col(RolePermissions::PermissionId)
        .to_owned();
    let mut fk_role = fk(
        "fk_rp_role",
        RolePermissions::Table,
        RolePermissions::RoleId,
        Roles::Table,
        Roles::Id,
    );
    let mut fk_perm = fk(
        "fk_rp_perm",
        RolePermissions::Table,
        RolePermissions::PermissionId,
        Permissions::Table,
        Permissions::Id,
    );
    m.create_table(
        Table::create()
            .table(RolePermissions::Table)
            .if_not_exists()
            .col(big(RolePermissions::RoleId))
            .col(big(RolePermissions::PermissionId))
            .primary_key(&mut pk)
            .foreign_key(&mut fk_role)
            .foreign_key(&mut fk_perm)
            .to_owned(),
    )
    .await
}

async fn create_user_roles(m: &SchemaManager<'_>) -> Result<(), DbErr> {
    let mut pk = Index::create()
        .name("pk_user_roles")
        .col(UserRoles::UserId)
        .col(UserRoles::RoleId)
        .to_owned();
    let mut fk_user = fk(
        "fk_ur_user",
        UserRoles::Table,
        UserRoles::UserId,
        Users::Table,
        Users::Id,
    );
    let mut fk_role = fk(
        "fk_ur_role",
        UserRoles::Table,
        UserRoles::RoleId,
        Roles::Table,
        Roles::Id,
    );
    m.create_table(
        Table::create()
            .table(UserRoles::Table)
            .if_not_exists()
            .col(big(UserRoles::UserId))
            .col(big(UserRoles::RoleId))
            .primary_key(&mut pk)
            .foreign_key(&mut fk_user)
            .foreign_key(&mut fk_role)
            .to_owned(),
    )
    .await
}

async fn create_projects(m: &SchemaManager<'_>) -> Result<(), DbErr> {
    let mut fk_supplier = fk(
        "fk_projects_supplier",
        Projects::Table,
        Projects::SupplierId,
        Suppliers::Table,
        Suppliers::Id,
    );
    m.create_table(
        Table::create()
            .table(Projects::Table)
            .if_not_exists()
            .col(id_pk(Projects::Id))
            .col(string(Projects::Code, 64).unique_key())
            .col(string(Projects::Name, 128))
            .col(string_null(Projects::Description, 1024))
            .col(big(Projects::SupplierId))
            .col(string_default(Projects::Status, 16, "DRAFT"))
            .col(big(Projects::CreatedBy))
            .col(ts(Projects::CreatedAt))
            .col(ts_updated(Projects::UpdatedAt))
            .foreign_key(&mut fk_supplier)
            .to_owned(),
    )
    .await?;
    for stmt in [
        Index::create()
            .name("idx_projects_supplier")
            .table(Projects::Table)
            .col(Projects::SupplierId)
            .to_owned(),
        Index::create()
            .name("idx_projects_status")
            .table(Projects::Table)
            .col(Projects::Status)
            .to_owned(),
    ] {
        m.create_index(stmt).await?;
    }
    Ok(())
}

async fn create_project_members(m: &SchemaManager<'_>) -> Result<(), DbErr> {
    let mut pk = Index::create()
        .name("pk_project_members")
        .col(ProjectMembers::ProjectId)
        .col(ProjectMembers::UserId)
        .to_owned();
    let mut fk_project = fk(
        "fk_pm_project",
        ProjectMembers::Table,
        ProjectMembers::ProjectId,
        Projects::Table,
        Projects::Id,
    );
    let mut fk_user = fk(
        "fk_pm_user",
        ProjectMembers::Table,
        ProjectMembers::UserId,
        Users::Table,
        Users::Id,
    );
    m.create_table(
        Table::create()
            .table(ProjectMembers::Table)
            .if_not_exists()
            .col(big(ProjectMembers::ProjectId))
            .col(big(ProjectMembers::UserId))
            .col(big_null(ProjectMembers::CreatedBy))
            .col(ts(ProjectMembers::CreatedAt))
            .primary_key(&mut pk)
            .foreign_key(&mut fk_project)
            .foreign_key(&mut fk_user)
            .to_owned(),
    )
    .await?;
    m.create_index(
        Index::create()
            .name("idx_pm_user")
            .table(ProjectMembers::Table)
            .col(ProjectMembers::UserId)
            .to_owned(),
    )
    .await
}

async fn create_rounds(m: &SchemaManager<'_>) -> Result<(), DbErr> {
    let mut fk_project = fk(
        "fk_rounds_project",
        Rounds::Table,
        Rounds::ProjectId,
        Projects::Table,
        Projects::Id,
    );
    m.create_table(
        Table::create()
            .table(Rounds::Table)
            .if_not_exists()
            .col(id_pk(Rounds::Id))
            .col(big(Rounds::ProjectId))
            .col(int(Rounds::RoundNo))
            .col(string_null(Rounds::Title, 128))
            .col(string_null(Rounds::Remark, 1024))
            .col(string(Rounds::ConfirmSide, 16))
            .col(string_default(Rounds::Status, 16, "PENDING"))
            .col(big_null(Rounds::DecidedBy))
            .col(ts_null(Rounds::DecidedAt))
            .col(string_null(Rounds::RejectReason, 1024))
            .col(big(Rounds::CreatedBy))
            .col(ts(Rounds::CreatedAt))
            .col(ts_updated(Rounds::UpdatedAt))
            .foreign_key(&mut fk_project)
            .to_owned(),
    )
    .await?;
    m.create_index(
        Index::create()
            .name("uk_rounds_project_no")
            .table(Rounds::Table)
            .col(Rounds::ProjectId)
            .col(Rounds::RoundNo)
            .unique()
            .to_owned(),
    )
    .await
}

async fn create_round_status_logs(m: &SchemaManager<'_>) -> Result<(), DbErr> {
    let mut fk_round = fk(
        "fk_rsl_round",
        RoundStatusLogs::Table,
        RoundStatusLogs::RoundId,
        Rounds::Table,
        Rounds::Id,
    );
    m.create_table(
        Table::create()
            .table(RoundStatusLogs::Table)
            .if_not_exists()
            .col(id_pk(RoundStatusLogs::Id))
            .col(big(RoundStatusLogs::RoundId))
            .col(string_null(RoundStatusLogs::FromStatus, 16))
            .col(string(RoundStatusLogs::ToStatus, 16))
            .col(string_null(RoundStatusLogs::Reason, 1024))
            .col(big(RoundStatusLogs::OperatorId))
            .col(ts(RoundStatusLogs::CreatedAt))
            .foreign_key(&mut fk_round)
            .to_owned(),
    )
    .await?;
    m.create_index(
        Index::create()
            .name("idx_rsl_round")
            .table(RoundStatusLogs::Table)
            .col(RoundStatusLogs::RoundId)
            .to_owned(),
    )
    .await
}

async fn create_files(m: &SchemaManager<'_>) -> Result<(), DbErr> {
    let mut fk_project = fk(
        "fk_files_project",
        Files::Table,
        Files::ProjectId,
        Projects::Table,
        Projects::Id,
    );
    let mut fk_round = fk(
        "fk_files_round",
        Files::Table,
        Files::RoundId,
        Rounds::Table,
        Rounds::Id,
    );
    let mut fk_uploader = fk(
        "fk_files_uploader",
        Files::Table,
        Files::UploaderId,
        Users::Table,
        Users::Id,
    );
    m.create_table(
        Table::create()
            .table(Files::Table)
            .if_not_exists()
            .col(id_pk(Files::Id))
            .col(big(Files::ProjectId))
            .col(big(Files::RoundId))
            .col(big(Files::UploaderId))
            .col(string(Files::Direction, 8))
            .col(string(Files::OriginalName, 255))
            .col(string(Files::StoredName, 64).unique_key())
            .col(string(Files::Ext, 16))
            .col(big(Files::SizeBytes))
            .col(string_null(Files::MimeType, 128))
            .col(string_null(Files::Sha256, 64))
            .col(string(Files::StoragePath, 512))
            .col(string_default(Files::Status, 16, "AVAILABLE"))
            .col(ts(Files::CreatedAt))
            .foreign_key(&mut fk_project)
            .foreign_key(&mut fk_round)
            .foreign_key(&mut fk_uploader)
            .to_owned(),
    )
    .await?;
    for stmt in [
        Index::create()
            .name("idx_files_round")
            .table(Files::Table)
            .col(Files::RoundId)
            .to_owned(),
        Index::create()
            .name("idx_files_project")
            .table(Files::Table)
            .col(Files::ProjectId)
            .to_owned(),
    ] {
        m.create_index(stmt).await?;
    }
    Ok(())
}

async fn create_upload_sessions(m: &SchemaManager<'_>) -> Result<(), DbErr> {
    let mut fk_project = fk(
        "fk_us_project",
        UploadSessions::Table,
        UploadSessions::ProjectId,
        Projects::Table,
        Projects::Id,
    );
    let mut fk_round = fk(
        "fk_us_round",
        UploadSessions::Table,
        UploadSessions::RoundId,
        Rounds::Table,
        Rounds::Id,
    );
    let mut fk_uploader = fk(
        "fk_us_uploader",
        UploadSessions::Table,
        UploadSessions::UploaderId,
        Users::Table,
        Users::Id,
    );
    m.create_table(
        Table::create()
            .table(UploadSessions::Table)
            .if_not_exists()
            .col(
                ColumnDef::new(UploadSessions::Id)
                    .string_len(36)
                    .not_null()
                    .primary_key()
                    .to_owned(),
            )
            .col(big(UploadSessions::ProjectId))
            .col(big(UploadSessions::RoundId))
            .col(big(UploadSessions::UploaderId))
            .col(string(UploadSessions::FileName, 255))
            .col(big(UploadSessions::FileSize))
            .col(string_null(UploadSessions::FileMd5, 32))
            .col(
                ColumnDef::new(UploadSessions::ChunkSize)
                    .unsigned()
                    .not_null()
                    .to_owned(),
            )
            .col(
                ColumnDef::new(UploadSessions::TotalChunks)
                    .unsigned()
                    .not_null()
                    .to_owned(),
            )
            .col(string(UploadSessions::TempDir, 512))
            .col(string_default(UploadSessions::Status, 16, "UPLOADING"))
            .col(
                ColumnDef::new(UploadSessions::ExpiresAt)
                    .date_time()
                    .not_null()
                    .to_owned(),
            )
            .col(ts(UploadSessions::CreatedAt))
            .col(ts_updated(UploadSessions::UpdatedAt))
            .foreign_key(&mut fk_project)
            .foreign_key(&mut fk_round)
            .foreign_key(&mut fk_uploader)
            .to_owned(),
    )
    .await?;
    for stmt in [
        Index::create()
            .name("idx_us_uploader")
            .table(UploadSessions::Table)
            .col(UploadSessions::UploaderId)
            .to_owned(),
        Index::create()
            .name("idx_us_status_exp")
            .table(UploadSessions::Table)
            .col(UploadSessions::Status)
            .col(UploadSessions::ExpiresAt)
            .to_owned(),
    ] {
        m.create_index(stmt).await?;
    }
    Ok(())
}

async fn create_messages(m: &SchemaManager<'_>) -> Result<(), DbErr> {
    let mut fk_project = fk(
        "fk_msg_project",
        Messages::Table,
        Messages::ProjectId,
        Projects::Table,
        Projects::Id,
    );
    let mut fk_round = fk(
        "fk_msg_round",
        Messages::Table,
        Messages::RoundId,
        Rounds::Table,
        Rounds::Id,
    );
    let mut fk_sender = fk(
        "fk_msg_sender",
        Messages::Table,
        Messages::SenderId,
        Users::Table,
        Users::Id,
    );
    m.create_table(
        Table::create()
            .table(Messages::Table)
            .if_not_exists()
            .col(id_pk(Messages::Id))
            .col(big(Messages::ProjectId))
            .col(big_null(Messages::RoundId))
            .col(big(Messages::SenderId))
            .col(text(Messages::Content))
            .col(string_default(Messages::Status, 16, "NORMAL"))
            .col(big_null(Messages::DeletedBy))
            .col(ts_null(Messages::DeletedAt))
            .col(ts(Messages::CreatedAt))
            .foreign_key(&mut fk_project)
            .foreign_key(&mut fk_round)
            .foreign_key(&mut fk_sender)
            .to_owned(),
    )
    .await?;
    for stmt in [
        Index::create()
            .name("idx_msg_project_time")
            .table(Messages::Table)
            .col(Messages::ProjectId)
            .col(Messages::CreatedAt)
            .to_owned(),
        Index::create()
            .name("idx_msg_round")
            .table(Messages::Table)
            .col(Messages::RoundId)
            .to_owned(),
    ] {
        m.create_index(stmt).await?;
    }
    Ok(())
}

async fn create_message_reads(m: &SchemaManager<'_>) -> Result<(), DbErr> {
    let mut pk = Index::create()
        .name("pk_message_reads")
        .col(MessageReads::MessageId)
        .col(MessageReads::UserId)
        .to_owned();
    let mut fk_msg = fk(
        "fk_mr_msg",
        MessageReads::Table,
        MessageReads::MessageId,
        Messages::Table,
        Messages::Id,
    );
    let mut fk_user = fk(
        "fk_mr_user",
        MessageReads::Table,
        MessageReads::UserId,
        Users::Table,
        Users::Id,
    );
    m.create_table(
        Table::create()
            .table(MessageReads::Table)
            .if_not_exists()
            .col(big(MessageReads::MessageId))
            .col(big(MessageReads::UserId))
            .col(ts(MessageReads::ReadAt))
            .primary_key(&mut pk)
            .foreign_key(&mut fk_msg)
            .foreign_key(&mut fk_user)
            .to_owned(),
    )
    .await?;
    m.create_index(
        Index::create()
            .name("idx_mr_user")
            .table(MessageReads::Table)
            .col(MessageReads::UserId)
            .to_owned(),
    )
    .await
}

async fn create_email_outbox(m: &SchemaManager<'_>) -> Result<(), DbErr> {
    let mut fk_project = fk(
        "fk_outbox_project",
        EmailOutbox::Table,
        EmailOutbox::ProjectId,
        Projects::Table,
        Projects::Id,
    );
    m.create_table(
        Table::create()
            .table(EmailOutbox::Table)
            .if_not_exists()
            .col(id_pk(EmailOutbox::Id))
            .col(string(EmailOutbox::EventType, 32))
            .col(big(EmailOutbox::ProjectId))
            .col(big_null(EmailOutbox::RoundId))
            .col(big_null(EmailOutbox::RecipientUserId))
            .col(string(EmailOutbox::RecipientEmail, 128))
            .col(string(EmailOutbox::Subject, 255))
            .col(text(EmailOutbox::Body))
            .col(string_default(EmailOutbox::Status, 16, "PENDING"))
            .col(int_default(EmailOutbox::RetryCount, 0))
            .col(string_null(EmailOutbox::LastError, 1024))
            .col(ts_null(EmailOutbox::SentAt))
            .col(ts(EmailOutbox::CreatedAt))
            .foreign_key(&mut fk_project)
            .to_owned(),
    )
    .await?;
    m.create_index(
        Index::create()
            .name("idx_outbox_status")
            .table(EmailOutbox::Table)
            .col(EmailOutbox::Status)
            .col(EmailOutbox::RetryCount)
            .to_owned(),
    )
    .await
}

async fn create_audit_logs(m: &SchemaManager<'_>) -> Result<(), DbErr> {
    m.create_table(
        Table::create()
            .table(AuditLogs::Table)
            .if_not_exists()
            .col(id_pk(AuditLogs::Id))
            .col(big_null(AuditLogs::UserId))
            .col(string_null(AuditLogs::Username, 64))
            .col(string(AuditLogs::Action, 48))
            .col(string_null(AuditLogs::TargetType, 32))
            .col(string_null(AuditLogs::TargetId, 64))
            .col(ColumnDef::new(AuditLogs::Detail).json().to_owned())
            .col(string_null(AuditLogs::Ip, 64))
            .col(ts(AuditLogs::CreatedAt))
            .to_owned(),
    )
    .await?;
    for stmt in [
        Index::create()
            .name("idx_audit_time")
            .table(AuditLogs::Table)
            .col(AuditLogs::CreatedAt)
            .to_owned(),
        Index::create()
            .name("idx_audit_user")
            .table(AuditLogs::Table)
            .col(AuditLogs::UserId)
            .to_owned(),
        Index::create()
            .name("idx_audit_action")
            .table(AuditLogs::Table)
            .col(AuditLogs::Action)
            .to_owned(),
    ] {
        m.create_index(stmt).await?;
    }
    Ok(())
}

async fn create_refresh_tokens(m: &SchemaManager<'_>) -> Result<(), DbErr> {
    let mut fk_user = fk(
        "fk_rt_user",
        RefreshTokens::Table,
        RefreshTokens::UserId,
        Users::Table,
        Users::Id,
    );
    m.create_table(
        Table::create()
            .table(RefreshTokens::Table)
            .if_not_exists()
            .col(id_pk(RefreshTokens::Id))
            .col(big(RefreshTokens::UserId))
            .col(string(RefreshTokens::TokenHash, 64).unique_key())
            .col(
                ColumnDef::new(RefreshTokens::ExpiresAt)
                    .date_time()
                    .not_null()
                    .to_owned(),
            )
            .col(bool_default(RefreshTokens::Revoked, false))
            .col(string_null(RefreshTokens::Ip, 64))
            .col(ts(RefreshTokens::CreatedAt))
            .foreign_key(&mut fk_user)
            .to_owned(),
    )
    .await
}

async fn create_system_configs(m: &SchemaManager<'_>) -> Result<(), DbErr> {
    m.create_table(
        Table::create()
            .table(SystemConfigs::Table)
            .if_not_exists()
            .col(
                ColumnDef::new(SystemConfigs::CfgKey)
                    .string_len(64)
                    .not_null()
                    .primary_key()
                    .to_owned(),
            )
            .col(string_null(SystemConfigs::CfgValue, 2048))
            .col(string_null(SystemConfigs::Description, 255))
            .col(ts_updated(SystemConfigs::UpdatedAt))
            .to_owned(),
    )
    .await
}

// ---------- Iden 定义（pub 供种子迁移复用） ----------

#[derive(DeriveIden)]
pub enum Suppliers {
    Table,
    Id,
    Name,
    Code,
    ContactName,
    ContactPhone,
    ContactEmail,
    Address,
    Remark,
    Status,
    CreatedBy,
    CreatedAt,
    UpdatedAt,
}

#[derive(DeriveIden)]
pub enum Departments {
    Table,
    Id,
    Name,
    ParentId,
    SortNo,
    Status,
    CreatedAt,
    UpdatedAt,
}

#[derive(DeriveIden)]
pub enum Users {
    Table,
    Id,
    Username,
    PasswordHash,
    RealName,
    Email,
    Phone,
    UserType,
    SupplierId,
    DepartmentId,
    Status,
    MustChangePassword,
    FailedLoginAttempts,
    LockedUntil,
    LastLoginAt,
    LastLoginIp,
    CreatedBy,
    CreatedAt,
    UpdatedAt,
}

#[derive(DeriveIden)]
pub enum Roles {
    Table,
    Id,
    Code,
    Name,
    Description,
    IsBuiltIn,
    Status,
    CreatedAt,
    UpdatedAt,
}

#[derive(DeriveIden)]
pub enum Permissions {
    Table,
    Id,
    Code,
    Name,
    Type,
    ParentId,
    SortNo,
}

#[derive(DeriveIden)]
pub enum RolePermissions {
    Table,
    RoleId,
    PermissionId,
}

#[derive(DeriveIden)]
pub enum UserRoles {
    Table,
    UserId,
    RoleId,
}

#[derive(DeriveIden)]
pub enum Projects {
    Table,
    Id,
    Code,
    Name,
    Description,
    SupplierId,
    Status,
    CreatedBy,
    CreatedAt,
    UpdatedAt,
}

#[derive(DeriveIden)]
pub enum ProjectMembers {
    Table,
    ProjectId,
    UserId,
    CreatedBy,
    CreatedAt,
}

#[derive(DeriveIden)]
pub enum Rounds {
    Table,
    Id,
    ProjectId,
    RoundNo,
    Title,
    Remark,
    ConfirmSide,
    Status,
    DecidedBy,
    DecidedAt,
    RejectReason,
    CreatedBy,
    CreatedAt,
    UpdatedAt,
}

#[derive(DeriveIden)]
pub enum RoundStatusLogs {
    Table,
    Id,
    RoundId,
    FromStatus,
    ToStatus,
    Reason,
    OperatorId,
    CreatedAt,
}

#[derive(DeriveIden)]
pub enum Files {
    Table,
    Id,
    ProjectId,
    RoundId,
    UploaderId,
    Direction,
    OriginalName,
    StoredName,
    Ext,
    SizeBytes,
    MimeType,
    Sha256,
    StoragePath,
    Status,
    CreatedAt,
}

#[derive(DeriveIden)]
pub enum UploadSessions {
    Table,
    Id,
    ProjectId,
    RoundId,
    UploaderId,
    FileName,
    FileSize,
    FileMd5,
    ChunkSize,
    TotalChunks,
    TempDir,
    Status,
    ExpiresAt,
    CreatedAt,
    UpdatedAt,
}

#[derive(DeriveIden)]
pub enum Messages {
    Table,
    Id,
    ProjectId,
    RoundId,
    SenderId,
    Content,
    Status,
    DeletedBy,
    DeletedAt,
    CreatedAt,
}

#[derive(DeriveIden)]
pub enum MessageReads {
    Table,
    MessageId,
    UserId,
    ReadAt,
}

#[derive(DeriveIden)]
pub enum EmailOutbox {
    Table,
    Id,
    EventType,
    ProjectId,
    RoundId,
    RecipientUserId,
    RecipientEmail,
    Subject,
    Body,
    Status,
    RetryCount,
    LastError,
    SentAt,
    CreatedAt,
}

#[derive(DeriveIden)]
pub enum AuditLogs {
    Table,
    Id,
    UserId,
    Username,
    Action,
    TargetType,
    TargetId,
    Detail,
    Ip,
    CreatedAt,
}

#[derive(DeriveIden)]
pub enum RefreshTokens {
    Table,
    Id,
    UserId,
    TokenHash,
    ExpiresAt,
    Revoked,
    Ip,
    CreatedAt,
}

#[derive(DeriveIden)]
pub enum SystemConfigs {
    Table,
    CfgKey,
    CfgValue,
    Description,
    UpdatedAt,
}
