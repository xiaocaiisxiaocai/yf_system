using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Yf.Api.Infrastructure.Entities;
using Yf.Api.Modules.Files;
using Yf.Api.Modules.Projects;

namespace Yf.Api.Infrastructure;

/// <summary>Explicit local development reset. Never called by normal startup or migration.</summary>
internal static class DevelopmentDataReset
{
    private static readonly string[] ClearedTables = [
        "oem_download_ranges", "oem_download_leases", "oem_download_sessions", "oem_file_promotions", "oem_file_scan_jobs",
        "oem_flow_tasks", "oem_flow_instance_nodes", "oem_flow_instances", "oem_upload_sessions", "oem_transfer_files",
        "oem_transfers", "oem_refresh_tokens", "oem_accounts", "oem_companies", "oem_flow_template_node_users", "oem_flow_template_scopes",
        "collaboration_reads", "message_reads", "message_images", "email_outbox", "project_activities",
        "file_copy_refs", "project_copy_jobs", "project_copies",
        "project_group_status_logs", "project_status_logs", "upload_sessions", "files", "file_blobs", "messages", "projects",
        "project_group_work_orders", "project_groups", "robot_parts", "refresh_tokens",
        "user_roles", "role_permissions", "departments", "audit_logs"
    ];

    private static readonly IReadOnlyDictionary<string, Func<YfDbContext, CancellationToken, Task<long>>> TableCounts =
        new Dictionary<string, Func<YfDbContext, CancellationToken, Task<long>>>(StringComparer.Ordinal)
        {
            ["oem_download_ranges"] = (db, ct) => db.OemDownloadRanges.LongCountAsync(ct),
            ["oem_download_leases"] = (db, ct) => db.OemDownloadLeases.LongCountAsync(ct),
            ["oem_download_sessions"] = (db, ct) => db.OemDownloadSessions.LongCountAsync(ct),
            ["oem_file_promotions"] = (db, ct) => db.OemFilePromotions.LongCountAsync(ct),
            ["oem_file_scan_jobs"] = (db, ct) => db.OemFileScanJobs.LongCountAsync(ct),
            ["oem_flow_tasks"] = (db, ct) => db.OemFlowTasks.LongCountAsync(ct),
            ["oem_flow_instance_nodes"] = (db, ct) => db.OemFlowInstanceNodes.LongCountAsync(ct),
            ["oem_flow_instances"] = (db, ct) => db.OemFlowInstances.LongCountAsync(ct),
            ["oem_upload_sessions"] = (db, ct) => db.OemUploadSessions.LongCountAsync(ct),
            ["oem_transfer_files"] = (db, ct) => db.OemTransferFiles.LongCountAsync(ct),
            ["oem_transfers"] = (db, ct) => db.OemTransfers.LongCountAsync(ct),
            ["oem_refresh_tokens"] = (db, ct) => db.OemRefreshTokens.LongCountAsync(ct),
            ["oem_accounts"] = (db, ct) => db.OemAccounts.LongCountAsync(ct),
            ["oem_companies"] = (db, ct) => db.OemCompanies.LongCountAsync(ct),
            ["oem_flow_template_node_users"] = (db, ct) => db.OemFlowTemplateNodeUsers.LongCountAsync(ct),
            ["oem_flow_template_scopes"] = (db, ct) => db.OemFlowTemplateScopes.LongCountAsync(ct),
            ["collaboration_reads"] = (db, ct) => db.CollaborationReads.LongCountAsync(ct),
            ["message_reads"] = (db, ct) => db.MessageReads.LongCountAsync(ct),
            ["message_images"] = (db, ct) => db.MessageImages.LongCountAsync(ct),
            ["email_outbox"] = (db, ct) => db.EmailOutbox.LongCountAsync(ct),
            ["project_activities"] = (db, ct) => db.ProjectActivities.LongCountAsync(ct),
            ["file_copy_refs"] = (db, ct) => db.FileCopyRefs.LongCountAsync(ct),
            ["project_copy_jobs"] = (db, ct) => db.ProjectCopyJobs.LongCountAsync(ct),
            ["project_copies"] = (db, ct) => db.ProjectCopies.LongCountAsync(ct),
            ["project_group_status_logs"] = (db, ct) => db.ProjectGroupStatusLogs.LongCountAsync(ct),
            ["project_status_logs"] = (db, ct) => db.ProjectStatusLogs.LongCountAsync(ct),
            ["upload_sessions"] = (db, ct) => db.UploadSessions.LongCountAsync(ct),
            ["files"] = (db, ct) => db.Files.LongCountAsync(ct),
            ["file_blobs"] = (db, ct) => db.FileBlobs.LongCountAsync(ct),
            ["messages"] = (db, ct) => db.Messages.LongCountAsync(ct),
            ["projects"] = (db, ct) => db.Projects.LongCountAsync(ct),
            ["project_group_work_orders"] = (db, ct) => db.ProjectGroupWorkOrders.LongCountAsync(ct),
            ["project_groups"] = (db, ct) => db.ProjectGroups.LongCountAsync(ct),
            ["robot_parts"] = (db, ct) => db.RobotParts.LongCountAsync(ct),
            ["refresh_tokens"] = (db, ct) => db.RefreshTokens.LongCountAsync(ct),
            ["user_roles"] = (db, ct) => db.UserRoles.LongCountAsync(ct),
            ["role_permissions"] = (db, ct) => db.RolePermissions.LongCountAsync(ct),
            ["departments"] = (db, ct) => db.Departments.LongCountAsync(ct),
            ["audit_logs"] = (db, ct) => db.AuditLogs.LongCountAsync(ct),
            ["users"] = (db, ct) => db.Users.LongCountAsync(ct),
            ["roles"] = (db, ct) => db.Roles.LongCountAsync(ct),
            ["suppliers"] = (db, ct) => db.Suppliers.LongCountAsync(ct),
            ["permissions"] = (db, ct) => db.Permissions.LongCountAsync(ct),
            ["system_configs"] = (db, ct) => db.SystemConfigs.LongCountAsync(ct),
        };

    internal sealed record Plan(
        string Database,
        string StorageRoot,
        string[] StorageDirectories,
        Dictionary<string, long> Counts,
        bool ResetCompleted = false,
        bool PasswordPreserved = true,
        bool SettingsPreserved = true,
        bool RobotCatalogWillBeReinitialized = true,
        int RobotCatalogSupplierCount = RobotPartCatalogSeed.SupplierCount,
        int RobotCatalogPartCount = RobotPartCatalogSeed.PartCount);

    private static (string Database, string Root) ValidateTarget(AppOptions options)
    {
        options.Validate();
        var db = new MySqlConnectionStringBuilder(options.ConnectionString);
        if (db.Server is not ("127.0.0.1" or "localhost" or "::1") || !new Uri(options.WebBaseUrl).IsLoopback)
            throw new InvalidOperationException("Development reset requires a loopback database and loopback website.");
        if (string.IsNullOrWhiteSpace(db.Database)) throw new InvalidOperationException("Database name is required.");
        var root = Path.GetFullPath(options.StorageRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (DirectoryInfo? directory = new(root); directory is not null; directory = directory.Parent)
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Development reset refuses storage paths containing links or reparse points.");
        return (db.Database, root);
    }

    internal static async Task<Plan> InspectAsync(AppOptions options, CancellationToken ct = default)
    {
        var (database, root) = ValidateTarget(options);
        await ValidateSchemaAsync(options, ct);
        await using var conn = await new AppDb(options).OpenAsync(ct);
        await using var db = EfDb.Use(conn);
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var table in ClearedTables.Concat(["users", "roles", "suppliers", "permissions", "system_configs"]))
            counts[table] = await TableCounts[table](db, ct);
        return new(database, root,
            [Path.Combine(root, "files"), Path.Combine(root, "message-images"), Path.Combine(root, "tmp"),
                Path.Combine(root, "copy-jobs"), Path.Combine(root, "blobs")], counts);
    }

    internal static async Task<Plan> ResetAsync(
        AppOptions options,
        string? confirmedDatabase,
        string? confirmedStorageRoot,
        CancellationToken ct = default)
    {
        var target = ValidateTarget(options);
        if (!string.Equals(confirmedDatabase, target.Database, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(confirmedStorageRoot)
            || !string.Equals(
                Path.GetFullPath(confirmedStorageRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                target.Root,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("Run --inspect-development-data first, then specify the exact --confirm-database and --confirm-storage-root. Stop the application before resetting.");
        var plan = await InspectAsync(options, ct);

        // Verify the exact managed directories and every descendant before any database write.
        foreach (var directory in plan.StorageDirectories)
        {
            FileStorage.EnsureLexicallyWithin(plan.StorageRoot, directory, false);
            if (!Directory.Exists(directory)) continue;
            var pending = new Stack<DirectoryInfo>();
            pending.Push(new DirectoryInfo(directory));
            while (pending.TryPop(out var entry))
            {
                ct.ThrowIfCancellationRequested();
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Storage cleanup refuses reparse points.");
                foreach (var child in entry.EnumerateFileSystemInfos())
                {
                    if ((child.Attributes & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidOperationException("Storage cleanup refuses reparse points.");
                    if (child is DirectoryInfo subdirectory) pending.Push(subdirectory);
                }
            }
        }

        await using (var conn = await new AppDb(options).OpenAsync(ct))
        await using (var gate = await MySqlNamedLock.TryAcquireAsync(
            conn, MySqlNamedLock.Name("development-reset", plan.Database), 0, ct)
            ?? throw new InvalidOperationException("Another development reset is running."))
        await using (var running = await AppRunningLease.TryAcquireAsync(conn, ct)
            ?? throw new InvalidOperationException("The application is still running against this database. Stop the site before resetting."))
        await using (var tx = await AppDb.BeginTransactionAsync(conn, ct))
        await using (var db = EfDb.Use(conn, tx))
        {
            await AccessService.LockManagementAsync(conn, tx, ct);
            var admin = await db.Users
                .FromSqlInterpolated($"SELECT * FROM users WHERE employee_no='admin' AND user_type='INTERNAL' FOR UPDATE")
                .AsNoTracking()
                .SingleOrDefaultAsync(ct)
                ?? throw new InvalidOperationException("Existing internal admin account is required; reset does not invent a password.");
            var role = await db.Roles
                .FromSqlInterpolated($"SELECT * FROM roles WHERE name='系统管理员' AND is_built_in=1 FOR UPDATE")
                .AsNoTracking()
                .SingleOrDefaultAsync(ct)
                ?? throw new InvalidOperationException("Existing system administrator role is required.");
            var settingsBefore = await SettingsSnapshotAsync(db, ct);

            await DeleteResetDataAsync(db, ct);

            await db.Users.Where(user => user.Id != admin.Id).ExecuteDeleteAsync(ct);
            await db.Roles.Where(candidate => candidate.Id != role.Id).ExecuteDeleteAsync(ct);
            await db.Suppliers.ExecuteDeleteAsync(ct);

            var catalog = RobotPartCatalogSeed.Load();
            var suppliers = catalog.Parts.DistinctBy(item => item.SupplierName, StringComparer.Ordinal)
                .Select(item => new Supplier
                {
                    Name = item.SupplierName,
                    Remark = item.SupplierRemark,
                    Status = AccountStatuses.Active,
                    CreatedBy = null,
                }).ToArray();
            db.Suppliers.AddRange(suppliers);
            await db.SaveChangesAsync(ct);
            var supplierIds = suppliers.ToDictionary(supplier => supplier.Name, supplier => supplier.Id, StringComparer.Ordinal);
            // Dictionaries survive the reset; only re-add preset Robot types that were deleted.
            var robotTypeNames = await db.ProjectDictionaries
                .Where(item => item.Type == ProjectDictionaryTypes.RobotType)
                .Select(item => item.Name).ToListAsync(ct);
            db.ProjectDictionaries.AddRange(catalog.RobotTypes
                .Where(type => !robotTypeNames.Contains(type.Name, StringComparer.OrdinalIgnoreCase))
                .Select(type => new ProjectDictionary
                {
                    Type = ProjectDictionaryTypes.RobotType,
                    Name = type.Name,
                    SortNo = type.SortNo,
                    Status = AccountStatuses.Active,
                }));
            db.RobotParts.AddRange(catalog.Parts.Select((item, index) => new RobotPart
            {
                SupplierId = supplierIds[item.SupplierName],
                PartNumber = item.PartNumber,
                Model = item.Model,
                SortNo = index + 1,
                Status = AccountStatuses.Active,
            }));
            await db.SaveChangesAsync(ct);

            var now = DateTime.UtcNow;
            await db.Users.Where(user => user.Id == admin.Id).ExecuteUpdateAsync(update => update
                .SetProperty(user => user.DepartmentId, (ulong?)null)
                .SetProperty(user => user.SupplierId, (ulong?)null)
                .SetProperty(user => user.Status, AccountStatuses.Active)
                .SetProperty(user => user.FailedLoginAttempts, 0)
                .SetProperty(user => user.LockedUntil, (DateTime?)null)
                .SetProperty(user => user.CreatedBy, (ulong?)null)
                .SetProperty(user => user.UpdatedAt, now), ct);
            await db.Roles.Where(candidate => candidate.Id == role.Id).ExecuteUpdateAsync(update => update
                .SetProperty(candidate => candidate.Status, AccountStatuses.Active)
                .SetProperty(candidate => candidate.UpdatedAt, now), ct);

            db.UserRoles.Add(new UserRole { UserId = admin.Id, RoleId = role.Id });
            var permissionIds = await db.Permissions.Select(permission => permission.Id).ToArrayAsync(ct);
            db.RolePermissions.AddRange(permissionIds.Select(permissionId => new RolePermission
            {
                RoleId = role.Id,
                PermissionId = permissionId,
            }));
            await db.SaveChangesAsync(ct);

            // OEM files live outside StorageRoot. Mark the independent OEM storage
            // for reconciliation after its database rows have been removed.
            await db.SystemConfigs.Where(config => config.CfgKey == "oem.storage.reconcile_required")
                .ExecuteUpdateAsync(update => update.SetProperty(config => config.CfgValue, "RESTORED"), ct);

            if (settingsBefore != await SettingsSnapshotAsync(db, ct)
                || admin.PasswordHash != await db.Users
                    .Where(user => user.Id == admin.Id)
                    .Select(user => user.PasswordHash)
                    .SingleAsync(ct))
                throw new InvalidOperationException("Password/settings preservation check failed; transaction will roll back.");
            await tx.CommitAsync(ct);
        }

        try
        {
            foreach (var directory in plan.StorageDirectories)
                FileStorage.DeleteDirectoryTree(plan.StorageRoot, directory, ct);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            throw new InvalidOperationException("Database reset committed; storage cleanup is incomplete. Re-run the same reset after resolving storage access.", error);
        }
        return (await InspectAsync(options, ct)) with { ResetCompleted = true };
    }

    private static async Task DeleteResetDataAsync(YfDbContext db, CancellationToken ct)
    {
        // Preserve the existing FK-safe order. ExecuteDelete bypasses tracking and
        // executes immediately inside the caller-owned transaction.
        // OEM business data first (children before parents). Approval and retention
        // templates are settings and survive; their people/organisation references do not.
        await db.OemDownloadRanges.ExecuteDeleteAsync(ct);
        await db.OemDownloadLeases.ExecuteDeleteAsync(ct);
        await db.OemDownloadSessions.ExecuteDeleteAsync(ct);
        await db.OemFilePromotions.ExecuteDeleteAsync(ct);
        await db.OemFileScanJobs.ExecuteDeleteAsync(ct);
        await db.OemFlowTasks.ExecuteDeleteAsync(ct);
        await db.OemFlowInstanceNodes.ExecuteDeleteAsync(ct);
        await db.OemFlowInstances.ExecuteDeleteAsync(ct);
        await db.OemUploadSessions.ExecuteDeleteAsync(ct);
        await db.OemTransferFiles.ExecuteDeleteAsync(ct);
        await db.OemTransfers.ExecuteDeleteAsync(ct);
        await db.OemRefreshTokens.ExecuteDeleteAsync(ct);
        await db.OemAccounts.ExecuteDeleteAsync(ct);
        await db.OemCompanies.ExecuteDeleteAsync(ct);
        await db.OemFlowTemplateNodeUsers.ExecuteDeleteAsync(ct);
        await db.OemFlowTemplateScopes.ExecuteDeleteAsync(ct);
        await db.CollaborationReads.ExecuteDeleteAsync(ct);
        await db.MessageReads.ExecuteDeleteAsync(ct);
        await db.MessageImages.ExecuteDeleteAsync(ct);
        await db.EmailOutbox.ExecuteDeleteAsync(ct);
        await db.ProjectActivities.ExecuteDeleteAsync(ct);
        await db.FileCopyRefs.ExecuteDeleteAsync(ct);
        await db.ProjectCopyJobs.ExecuteDeleteAsync(ct);
        await db.ProjectCopies.ExecuteDeleteAsync(ct);
        await db.ProjectGroupStatusLogs.ExecuteDeleteAsync(ct);
        await db.ProjectStatusLogs.ExecuteDeleteAsync(ct);
        await db.UploadSessions.ExecuteDeleteAsync(ct);
        await db.Files.ExecuteDeleteAsync(ct);
        await db.FileBlobs.ExecuteDeleteAsync(ct);
        await db.Messages.ExecuteDeleteAsync(ct);
        await db.Projects.ExecuteDeleteAsync(ct);
        await db.ProjectGroupWorkOrders.ExecuteDeleteAsync(ct);
        await db.ProjectGroups.ExecuteDeleteAsync(ct);
        await db.RobotParts.ExecuteDeleteAsync(ct);
        await db.RefreshTokens.ExecuteDeleteAsync(ct);
        await db.UserRoles.ExecuteDeleteAsync(ct);
        await db.RolePermissions.ExecuteDeleteAsync(ct);
        await db.Departments.ExecuteDeleteAsync(ct);
        await db.AuditLogs.ExecuteDeleteAsync(ct);
    }

    private static async Task<string> SettingsSnapshotAsync(YfDbContext db, CancellationToken ct) =>
        JsonSerializer.Serialize(await db.SystemConfigs
            .Where(config => config.CfgKey != "security.identity_revision"
                && config.CfgKey != "oem.storage.reconcile_required")
            .OrderBy(config => config.CfgKey)
            .Select(config => new { config.CfgKey, config.CfgValue, config.Description, config.UpdatedAt })
            .ToArrayAsync(ct));

    // Keep schema lifecycle coupling in one place so the migration cutover only
    // needs to replace this call, without touching reset authorization or cleanup.
    private static Task ValidateSchemaAsync(AppOptions options, CancellationToken ct) =>
        SchemaBootstrap.ValidateAsync(new AppDb(options), ct);
}
