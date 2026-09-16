using Dapper;
using MySqlConnector;
using System.Text.Json;
using Yf.Api.Modules.Files;

namespace Yf.Api.Infrastructure;

/// <summary>Explicit local development reset. Never called by normal startup or migration.</summary>
internal static class DevelopmentDataReset
{
    private static readonly string[] ClearedTables = [
        "collaboration_reads", "message_reads", "message_images", "email_outbox", "project_activities",
        "file_copy_refs", "project_copies",
        "project_group_status_logs", "project_status_logs", "upload_sessions", "files", "messages", "projects",
        "project_group_work_orders", "project_groups", "refresh_tokens",
        "user_roles", "role_permissions", "departments", "audit_logs"
    ];
    internal sealed record Plan(string Database, string StorageRoot, string[] StorageDirectories,
        Dictionary<string, long> Counts, bool ResetCompleted = false, bool PasswordPreserved = true, bool SettingsPreserved = true);

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
        await SchemaBootstrap.ValidateAsync(new AppDb(options), ct);
        await using var conn = await new AppDb(options).OpenAsync(ct);
        var counts = new Dictionary<string, long>();
        foreach (var table in ClearedTables.Concat(["users", "roles", "suppliers", "permissions", "system_configs"]))
            counts[table] = await conn.ExecuteScalarAsync<long>(new CommandDefinition($"SELECT COUNT(*) FROM `{table}`", cancellationToken: ct));
        return new(database, root,
            [Path.Combine(root, "files"), Path.Combine(root, "message-images"), Path.Combine(root, "tmp")], counts);
    }

    internal static async Task<Plan> ResetAsync(AppOptions options, string? confirmedDatabase,
        string? confirmedStorageRoot, CancellationToken ct = default)
    {
        var target = ValidateTarget(options);
        if (!string.Equals(confirmedDatabase, target.Database, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(confirmedStorageRoot)
            || !string.Equals(Path.GetFullPath(confirmedStorageRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                target.Root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
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
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Storage cleanup refuses reparse points.");
                foreach (var child in entry.EnumerateFileSystemInfos())
                {
                    if ((child.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Storage cleanup refuses reparse points.");
                    if (child is DirectoryInfo subdirectory) pending.Push(subdirectory);
                }
            }
        }

        {
            await using var conn = await new AppDb(options).OpenAsync(ct);
            await using var gate = await MySqlNamedLock.TryAcquireAsync(conn, MySqlNamedLock.Name("development-reset", plan.Database), 0, ct)
                ?? throw new InvalidOperationException("Another development reset is running.");
            await using (var tx = await AppDb.BeginTransactionAsync(conn, ct))
            {
                await AccessService.LockManagementAsync(conn, tx, ct);
                var admin = await conn.QuerySingleOrDefaultAsync<Admin>(new CommandDefinition(
                    "SELECT id AS Id,password_hash AS PasswordHash FROM users WHERE employee_no='admin' AND user_type='INTERNAL' FOR UPDATE", transaction: tx, cancellationToken: ct))
                    ?? throw new InvalidOperationException("Existing internal admin account is required; reset does not invent a password.");
                var role = await conn.QuerySingleOrDefaultAsync<ulong?>(new CommandDefinition(
                    "SELECT id FROM roles WHERE name='系统管理员' AND is_built_in=1 FOR UPDATE", transaction: tx, cancellationToken: ct))
                    ?? throw new InvalidOperationException("Existing system administrator role is required.");
                var settingsBefore = await SettingsSnapshotAsync(conn, tx, ct);
                foreach (var table in ClearedTables)
                    await conn.ExecuteAsync(new CommandDefinition($"DELETE FROM `{table}`", transaction: tx, cancellationToken: ct));
                await conn.ExecuteAsync(new CommandDefinition("DELETE FROM users WHERE id<>@Id; DELETE FROM roles WHERE id<>@Role; DELETE FROM suppliers;", new { admin.Id, Role = role }, tx, cancellationToken: ct));
                await conn.ExecuteAsync(new CommandDefinition("""
                    UPDATE users SET department_id=NULL,supplier_id=NULL,status='ACTIVE',failed_login_attempts=0,locked_until=NULL,created_by=NULL,updated_at=UTC_TIMESTAMP(6) WHERE id=@Id;
                    UPDATE roles SET status='ACTIVE',updated_at=UTC_TIMESTAMP(6) WHERE id=@Role;
                    INSERT INTO user_roles(user_id,role_id) VALUES(@Id,@Role);
                    INSERT INTO role_permissions(role_id,permission_id) SELECT @Role,id FROM permissions;
                    """, new { admin.Id, Role = role }, tx, cancellationToken: ct));
                if (settingsBefore != await SettingsSnapshotAsync(conn, tx, ct)
                    || admin.PasswordHash != await conn.ExecuteScalarAsync<string>(new CommandDefinition("SELECT password_hash FROM users WHERE id=@Id", new { admin.Id }, tx, cancellationToken: ct)))
                    throw new InvalidOperationException("Password/settings preservation check failed; transaction will roll back.");
                await tx.CommitAsync(ct);
            }
        }
        try
        {
            foreach (var directory in plan.StorageDirectories)
                await FileStorage.DeleteDirectoryTreeAsync(plan.StorageRoot, directory, ct);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            throw new InvalidOperationException("Database reset committed; storage cleanup is incomplete. Re-run the same reset after resolving storage access.", error);
        }
        return (await InspectAsync(options, ct)) with { ResetCompleted = true };
    }

    private static async Task<string> SettingsSnapshotAsync(MySqlConnection conn, MySqlTransaction tx, CancellationToken ct) =>
        JsonSerializer.Serialize((await conn.QueryAsync(new CommandDefinition("SELECT * FROM system_configs ORDER BY cfg_key", transaction: tx, cancellationToken: ct))).Select(row => (IDictionary<string, object>)row));
    private sealed class Admin { public ulong Id { get; init; } public string PasswordHash { get; init; } = ""; }
}
