using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;

namespace Yf.Api.Infrastructure;

public sealed class AppDb
{
    private readonly string connectionString;

    internal string WebBaseUrl { get; }

    public AppDb(AppOptions options)
    {
        // Parse and validate once when the singleton is constructed. Besides failing fast, this
        // avoids rebuilding the same connection string for every request connection.
        connectionString = BuildConnectionString(options);
        WebBaseUrl = options.WebBaseUrl;
    }

    // Business writes serialize on the management gate and then the project/user
    // row. Reads made after waiting for those locks must see the latest commit,
    // rather than a REPEATABLE READ snapshot created by the actor pre-check.
    public static ValueTask<MySqlTransaction> BeginTransactionAsync(MySqlConnection connection, CancellationToken ct = default)
        => connection.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);

    public async Task<MySqlConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = new MySqlConnection(connectionString);
        try { await connection.OpenAsync(cancellationToken); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }

    // Shared by raw-connection callers and the EF Core YfDbContext registration
    // (ApiApplication.cs), so both open connections with identical settings and
    // pass the same TLS/transport validation.
    public static string BuildConnectionString(AppOptions options)
    {
        var builder = new MySqlConnectionStringBuilder(options.ConnectionString)
        {
            DateTimeKind = MySqlDateTimeKind.Utc,
            ConnectionTimeout = 10,
            DefaultCommandTimeout = 30,
            // Pomelo requires these settings before a connection is opened;
            // otherwise it cannot join a transaction owned by a shared helper.
            AllowUserVariables = true,
            UseAffectedRows = false
        };
        DatabaseTransportPolicy.Validate(builder);
        return builder.ConnectionString;
    }
}

public sealed class ApiException(int status, int code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public int Code { get; } = code;
    public static ApiException BadRequest(string message) => new(400, 40001, message);
    public static ApiException Unauthorized(string message = "缺少登录凭证") => new(401, 40101, message);
    public static ApiException Forbidden(string message = "无操作权限") => new(403, 40301, message);
    public static ApiException OutOfScope(string message = "无权访问该数据") => new(403, 40302, message);
    public static ApiException NotFound(string message = "资源不存在") => new(404, 40401, message);
    public static ApiException Conflict(string message) => new(409, 40901, message);
    /// <summary>A database lock wait timed out or a deadlock victim was rolled back; the request may simply be retried.</summary>
    public static ApiException Busy(string message = "系统繁忙，数据正被其他操作占用，请稍后重试") => new(409, 40902, message);
    public static ApiException TooManyRequests(string message) => new(429, 42902, message);
}

public sealed record CurrentUser(ulong Id, string EmployeeNo, string UserType, ulong? SupplierId)
{
    public ulong UserId => Id;
    public bool IsInternal => UserType == UserTypes.Internal;
}

public sealed class AccessService
{
    public static void RequireInternal(CurrentUser user)
    {
        if (!user.IsInternal) throw ApiException.Forbidden("仅内部用户可以访问管理功能");
    }

    public static CurrentUser GetCurrent(HttpContext context) =>
        context.Items.TryGetValue(typeof(CurrentUser), out var actor) && actor is CurrentUser user ? user : throw ApiException.Unauthorized();

    /// <summary>
    /// Exclusive gate: only for writes that change who may do what (accounts, roles, permissions,
    /// organizations, suppliers, security/system settings, dictionaries). It waits for, and blocks,
    /// every business transaction. Project data writes take <see cref="LockBusinessAsync"/> plus the
    /// project-group/project row locks instead, so they do not stall the whole system.
    /// </summary>
    public static async Task LockManagementAsync(MySqlConnection db, MySqlTransaction tx, CancellationToken ct = default)
    {
        await using var context = EfDb.Use(db, tx);
        var gate = await context.Database.SqlQuery<string>(
            $"SELECT cfg_key AS Value FROM system_configs WHERE cfg_key='security.management_lock' FOR UPDATE")
            .SingleOrDefaultAsync(ct);
        if (gate is null) throw new InvalidOperationException("Management gate missing; EF database initialization is required.");
    }

    public static async Task LockBusinessAsync(MySqlConnection db, MySqlTransaction tx, CancellationToken ct = default)
    {
        await using var context = EfDb.Use(db, tx);
        var gate = await context.Database.SqlQuery<string>(
            $"SELECT cfg_key AS Value FROM system_configs WHERE cfg_key='security.management_lock' LOCK IN SHARE MODE")
            .SingleOrDefaultAsync(ct);
        if (gate is null) throw new InvalidOperationException("Management gate missing; EF database initialization is required.");
        // Every role/permission/user-status change holds the gate exclusively, so while this
        // transaction holds it shared, permission grants cannot change and may be cached.
        PermissionCache.GetValue(tx, static _ => new ConcurrentDictionary<ulong, PermissionSnapshot>());
    }

    /// <summary>
    /// Permission codes per user, scoped to one transaction that holds the business gate in share mode
    /// (see <see cref="LockBusinessAsync"/>). A request checks several permission points; each check
    /// would otherwise re-run the same four-table join.
    /// </summary>
    private static readonly ConditionalWeakTable<MySqlTransaction, ConcurrentDictionary<ulong, PermissionSnapshot>> PermissionCache = new();

    public static async Task<CurrentUser> RecheckActorAsync(MySqlConnection db, MySqlTransaction? tx, CurrentUser user, CancellationToken ct = default)
    {
        await using var context = EfDb.Use(db, tx);
        var row = await context.Users.Where(row => row.Id == user.Id).Select(row => new
        {
            row.Id, row.EmployeeNo, row.UserType, row.SupplierId, row.Status, row.MustChangePassword,
        }).SingleOrDefaultAsync(ct);
        if (row is null || row.Status != AccountStatuses.Active || row.MustChangePassword) throw ApiException.Forbidden();
        if (row.UserType == UserTypes.Supplier && (row.SupplierId is not ulong supplierId
            || !await context.Suppliers.AnyAsync(supplier => supplier.Id == supplierId && supplier.Status == AccountStatuses.Active, ct)))
            throw ApiException.Forbidden();
        return new(row.Id, row.EmployeeNo, row.UserType, row.SupplierId);
    }

    public static async Task<CurrentUser> LockActorAsync(MySqlConnection db, MySqlTransaction tx, CurrentUser user, CancellationToken ct = default)
    {
        await LockBusinessAsync(db, tx, ct);
        return await RecheckActorAsync(db, tx, user, ct);
    }

    /// <summary>
    /// Revalidates the session actor for a read without taking the global management gate. Permission
    /// and data-scope queries made after this call remain live under READ COMMITTED; they are deliberately
    /// not transaction-cached unless the caller separately holds <see cref="LockBusinessAsync"/>. This is
    /// a check at the query boundary, not a lease that can revoke an already-started response stream.
    /// </summary>
    public static Task<CurrentUser> ReadActorAsync(
        MySqlConnection db, MySqlTransaction? tx, CurrentUser user, CancellationToken ct = default) =>
        RecheckActorAsync(db, tx, user, ct);

    public static async Task<string[]> PermissionCodesAsync(MySqlConnection db, MySqlTransaction? tx, ulong userId, CancellationToken ct = default)
        => (await PermissionSnapshotAsync(db, tx, userId, ct)).Codes;

    internal static async Task<(IReadOnlyList<string> Codes, IReadOnlyList<string> Menus)> PermissionCodesAndMenusAsync(
        MySqlConnection db, MySqlTransaction? tx, ulong userId, CancellationToken ct = default)
    {
        var snapshot = await PermissionSnapshotAsync(db, tx, userId, ct);
        return (snapshot.Codes, snapshot.Menus);
    }

    private static async Task<PermissionSnapshot> PermissionSnapshotAsync(
        MySqlConnection db, MySqlTransaction? tx, ulong userId, CancellationToken ct)
    {
        if (tx is not null && PermissionCache.TryGetValue(tx, out var cache))
        {
            if (cache.TryGetValue(userId, out var cached)) return cached;
            var loaded = await LoadPermissionSnapshotAsync(db, tx, userId, ct);
            cache[userId] = loaded;
            return loaded;
        }
        return await LoadPermissionSnapshotAsync(db, tx, userId, ct);
    }

    /// <summary>Whether <paramref name="userId"/> holds <paramref name="permission"/> through an active role.</summary>
    public static async Task<bool> HasPermissionAsync(MySqlConnection db, MySqlTransaction? tx, ulong userId, string permission, CancellationToken ct = default) =>
        (await PermissionCodesAsync(db, tx, userId, ct)).Contains(permission, StringComparer.Ordinal);

    /// <summary>Composable effective grants. Callers can keep recipient authorization in one bulk SQL query.</summary>
    internal static IQueryable<EffectivePermissionGrant> EffectivePermissionGrants(YfDbContext context) =>
        context.UserRoles
            .Join(context.Roles.Where(role => role.Status == AccountStatuses.Active),
                userRole => userRole.RoleId, role => role.Id, (userRole, _) => userRole)
            .Join(context.RolePermissions,
                userRole => userRole.RoleId, rolePermission => rolePermission.RoleId,
                (userRole, rolePermission) => new { userRole.UserId, rolePermission.PermissionId })
            .Join(context.Permissions,
                grant => grant.PermissionId, permission => permission.Id,
                (grant, permission) => new EffectivePermissionGrant
                {
                    UserId = grant.UserId,
                    PermissionId = permission.Id,
                    Code = permission.Code,
                    Type = permission.Type,
                    SortNo = permission.SortNo,
                });

    internal static IQueryable<ulong> UsersWithPermission(YfDbContext context, string permission) =>
        EffectivePermissionGrants(context)
            .Where(grant => grant.Code == permission)
            .Select(grant => grant.UserId)
            .Distinct();

    private static async Task<PermissionSnapshot> LoadPermissionSnapshotAsync(
        MySqlConnection db, MySqlTransaction? tx, ulong userId, CancellationToken ct)
    {
        await using var context = EfDb.Use(db, tx);
        var grants = await EffectivePermissionGrants(context)
            .Where(grant => grant.UserId == userId)
            .Select(grant => new { grant.PermissionId, grant.Code, grant.Type, grant.SortNo })
            .Distinct()
            .ToArrayAsync(ct);
        return new(
            grants.Select(grant => grant.Code).Order(StringComparer.Ordinal).ToArray(),
            grants.Where(grant => grant.Type == "MENU")
                .OrderBy(grant => grant.SortNo).ThenBy(grant => grant.PermissionId)
                .Select(grant => grant.Code).ToArray());
    }

    public static async Task RequirePermissionAsync(MySqlConnection db, MySqlTransaction? tx, CurrentUser user, string permission, CancellationToken ct = default)
    {
        if (!await HasPermissionAsync(db, tx, user.Id, permission, ct)) throw ApiException.Forbidden();
    }

    public static async Task<bool> IsSystemAdminAsync(MySqlConnection db, MySqlTransaction? tx, ulong userId, CancellationToken ct = default)
    {
        await using var context = EfDb.Use(db, tx);
        return await context.UserRoles.Where(userRole => userRole.UserId == userId)
            .Join(context.Roles.Where(role => role.Status == AccountStatuses.Active
                && role.IsBuiltIn && role.Name == BuiltInRoleNames.SystemAdministrator),
                userRole => userRole.RoleId, role => role.Id, (_, _) => true)
            .AnyAsync(ct);
    }

    internal sealed class EffectivePermissionGrant
    {
        public ulong UserId { get; init; }
        public ulong PermissionId { get; init; }
        public string Code { get; init; } = "";
        public string Type { get; init; } = "";
        public int SortNo { get; init; }
    }

    private sealed record PermissionSnapshot(string[] Codes, string[] Menus);
}
