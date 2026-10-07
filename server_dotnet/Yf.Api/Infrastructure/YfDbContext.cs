using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Migrations;
using Yf.Api.Infrastructure.Entities;
using Yf.Api.Modules.Oem.Data;

namespace Yf.Api.Infrastructure;

/// <summary>
/// Code-first EF Core model and the sole authority for current schema evolution.
/// The stable InitialCreate migration is the empty-database baseline.
///
/// Historical migration tables and one-time migration artifacts are not entities.
/// </summary>
public sealed class YfDbContext(DbContextOptions<YfDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<Department> Departments => Set<Department>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();

    public DbSet<ProjectGroup> ProjectGroups => Set<ProjectGroup>();
    public DbSet<ProjectGroupWorkOrder> ProjectGroupWorkOrders => Set<ProjectGroupWorkOrder>();
    public DbSet<ProjectGroupStatusLog> ProjectGroupStatusLogs => Set<ProjectGroupStatusLog>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<ProjectWorkOrder> ProjectWorkOrders => Set<ProjectWorkOrder>();
    public DbSet<ProjectStatusLog> ProjectStatusLogs => Set<ProjectStatusLog>();
    public DbSet<ProjectDictionary> ProjectDictionaries => Set<ProjectDictionary>();
    public DbSet<RobotPart> RobotParts => Set<RobotPart>();
    public DbSet<ProjectCopy> ProjectCopies => Set<ProjectCopy>();
    public DbSet<ProjectCopyJob> ProjectCopyJobs => Set<ProjectCopyJob>();
    public DbSet<ProjectCopyWorkerState> ProjectCopyWorkerStates => Set<ProjectCopyWorkerState>();
    public DbSet<ProjectActivity> ProjectActivities => Set<ProjectActivity>();
    public DbSet<CollaborationRead> CollaborationReads => Set<CollaborationRead>();

    public DbSet<Message> Messages => Set<Message>();
    public DbSet<MessageRead> MessageReads => Set<MessageRead>();
    public DbSet<MessageImage> MessageImages => Set<MessageImage>();

    public DbSet<FileRecord> Files => Set<FileRecord>();
    public DbSet<FileBlob> FileBlobs => Set<FileBlob>();
    public DbSet<UploadSession> UploadSessions => Set<UploadSession>();
    public DbSet<FileCopyRef> FileCopyRefs => Set<FileCopyRef>();

    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<EmailOutbox> EmailOutbox => Set<EmailOutbox>();
    public DbSet<SystemConfig> SystemConfigs => Set<SystemConfig>();

    // OEM business line (independent from the supplier collaboration tables above).
    public DbSet<OemCompany> OemCompanies => Set<OemCompany>();
    public DbSet<OemAccount> OemAccounts => Set<OemAccount>();
    public DbSet<OemRefreshToken> OemRefreshTokens => Set<OemRefreshToken>();
    public DbSet<OemRetentionTemplate> OemRetentionTemplates => Set<OemRetentionTemplate>();
    public DbSet<OemTransfer> OemTransfers => Set<OemTransfer>();
    public DbSet<OemTransferFile> OemTransferFiles => Set<OemTransferFile>();
    public DbSet<OemUploadSession> OemUploadSessions => Set<OemUploadSession>();
    public DbSet<OemFileScanJob> OemFileScanJobs => Set<OemFileScanJob>();
    public DbSet<OemFilePromotion> OemFilePromotions => Set<OemFilePromotion>();
    public DbSet<OemFlowTemplate> OemFlowTemplates => Set<OemFlowTemplate>();
    public DbSet<OemFlowTemplateScope> OemFlowTemplateScopes => Set<OemFlowTemplateScope>();
    public DbSet<OemFlowTemplateNode> OemFlowTemplateNodes => Set<OemFlowTemplateNode>();
    public DbSet<OemFlowTemplateNodeUser> OemFlowTemplateNodeUsers => Set<OemFlowTemplateNodeUser>();
    public DbSet<OemFlowInstance> OemFlowInstances => Set<OemFlowInstance>();
    public DbSet<OemFlowInstanceNode> OemFlowInstanceNodes => Set<OemFlowInstanceNode>();
    public DbSet<OemFlowTask> OemFlowTasks => Set<OemFlowTask>();
    public DbSet<OemDownloadSession> OemDownloadSessions => Set<OemDownloadSession>();
    public DbSet<OemDownloadRange> OemDownloadRanges => Set<OemDownloadRange>();
    public DbSet<OemDownloadLease> OemDownloadLeases => Set<OemDownloadLease>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(YfDbContext).Assembly);

        // Repo-wide convention: every single-column primary key is the C# property
        // `Id` mapping to the snake_case column `id`. Applied once here instead of
        // repeating `.Property(x => x.Id).HasColumnName("id")` in all ~20 configs.
        // Composite-key entities (RolePermission, UserRole, MessageRead, CollaborationRead)
        // and the string-keyed ones (UploadSession.Id, SystemConfig.CfgKey) set their
        // own column names explicitly and are untouched by this loop.
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            var idProperty = entityType.FindProperty("Id");
            if (idProperty is not null && idProperty.GetColumnName() == "Id")
                idProperty.SetColumnName("id");
        }
    }
}

/// <summary>
/// Lets `dotnet ef migrations add` run without booting the full app/DI pipeline.
/// Design-time only: reads a plain connection string from YF_EF_DESIGN_CONNECTION
/// (never a business database -- point this at a disposable local database).
/// </summary>
public sealed class YfDbContextFactory : IDesignTimeDbContextFactory<YfDbContext>
{
    public YfDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("YF_EF_DESIGN_CONNECTION")
            ?? throw new InvalidOperationException("Set YF_EF_DESIGN_CONNECTION to a disposable local MySQL connection string for design-time tooling.");
        var builder = new DbContextOptionsBuilder<YfDbContext>();
        builder.UseMySql(connectionString, EfDb.ServerVersion)
            .ReplaceService<IMigrator, PreflightMySqlMigrator>()
            .AddInterceptors(UtcDatabaseSession.Instance);
        return new YfDbContext(builder.Options);
    }
}
