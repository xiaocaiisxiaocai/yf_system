using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;

namespace Yf.Api.Infrastructure;

public sealed class AppDb(AppOptions options)
{
    internal string WebBaseUrl => options.WebBaseUrl;

    // Business writes serialize on the management gate and then the project/user
    // row. Reads made after waiting for those locks must see the latest commit,
    // rather than a REPEATABLE READ snapshot created by the actor pre-check.
    public static ValueTask<MySqlTransaction> BeginTransactionAsync(MySqlConnection connection, CancellationToken ct = default)
        => connection.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);

    public async Task<MySqlConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = new MySqlConnection(BuildConnectionString(options));
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
    public bool IsInternal => UserType == "INTERNAL";
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
        PermissionCache.AddOrUpdate(tx, new ConcurrentDictionary<ulong, string[]>());
    }

    /// <summary>
    /// Permission codes per user, scoped to one transaction that holds the business gate in share mode
    /// (see <see cref="LockBusinessAsync"/>). A request checks several permission points; each check
    /// would otherwise re-run the same four-table join.
    /// </summary>
    private static readonly ConditionalWeakTable<MySqlTransaction, ConcurrentDictionary<ulong, string[]>> PermissionCache = new();

    public static async Task<CurrentUser> RecheckActorAsync(MySqlConnection db, MySqlTransaction tx, CurrentUser user, CancellationToken ct = default)
    {
        await using var context = EfDb.Use(db, tx);
        var row = await context.Users.Where(row => row.Id == user.Id).Select(row => new
        {
            row.Id, row.EmployeeNo, row.UserType, row.SupplierId, row.Status, row.MustChangePassword,
        }).SingleOrDefaultAsync(ct);
        if (row is null || row.Status != "ACTIVE" || row.MustChangePassword) throw ApiException.Forbidden();
        if (row.UserType == "SUPPLIER" && (row.SupplierId is not ulong supplierId
            || !await context.Suppliers.AnyAsync(supplier => supplier.Id == supplierId && supplier.Status == "ACTIVE", ct)))
            throw ApiException.Forbidden();
        return new(row.Id, row.EmployeeNo, row.UserType, row.SupplierId);
    }

    public static async Task<CurrentUser> LockActorAsync(MySqlConnection db, MySqlTransaction tx, CurrentUser user, CancellationToken ct = default)
    {
        await LockBusinessAsync(db, tx, ct);
        return await RecheckActorAsync(db, tx, user, ct);
    }

    public static async Task<string[]> PermissionCodesAsync(MySqlConnection db, MySqlTransaction? tx, ulong userId, CancellationToken ct = default)
    {
        if (tx is not null && PermissionCache.TryGetValue(tx, out var cache))
        {
            if (cache.TryGetValue(userId, out var cached)) return cached;
            var loaded = await LoadPermissionCodesAsync(db, tx, userId, ct);
            cache[userId] = loaded;
            return loaded;
        }
        return await LoadPermissionCodesAsync(db, tx, userId, ct);
    }

    /// <summary>Whether <paramref name="userId"/> holds <paramref name="permission"/> through an active role.</summary>
    public static async Task<bool> HasPermissionAsync(MySqlConnection db, MySqlTransaction? tx, ulong userId, string permission, CancellationToken ct = default) =>
        (await PermissionCodesAsync(db, tx, userId, ct)).Contains(permission, StringComparer.Ordinal);

    private static async Task<string[]> LoadPermissionCodesAsync(MySqlConnection db, MySqlTransaction? tx, ulong userId, CancellationToken ct)
    {
        await using var context = EfDb.Use(db, tx);
        return await context.UserRoles.Where(userRole => userRole.UserId == userId)
            .Join(context.Roles.Where(role => role.Status == "ACTIVE"), userRole => userRole.RoleId, role => role.Id, (userRole, _) => userRole)
            .Join(context.RolePermissions, userRole => userRole.RoleId, rolePermission => rolePermission.RoleId, (_, rolePermission) => rolePermission)
            .Join(context.Permissions, rolePermission => rolePermission.PermissionId, permission => permission.Id, (_, permission) => permission.Code)
            .Distinct().ToArrayAsync(ct);
    }

    public static async Task RequirePermissionAsync(MySqlConnection db, MySqlTransaction? tx, CurrentUser user, string permission, CancellationToken ct = default)
    {
        if (!await HasPermissionAsync(db, tx, user.Id, permission, ct)) throw ApiException.Forbidden();
    }

    public static async Task<bool> IsSystemAdminAsync(MySqlConnection db, MySqlTransaction? tx, ulong userId, CancellationToken ct = default)
    {
        await using var context = EfDb.Use(db, tx);
        return await context.UserRoles.Where(userRole => userRole.UserId == userId)
            .Join(context.Roles.Where(role => role.Status == "ACTIVE" && role.IsBuiltIn && role.Name == "系统管理员"),
                userRole => userRole.RoleId, role => role.Id, (_, _) => true)
            .AnyAsync(ct);
    }
}
