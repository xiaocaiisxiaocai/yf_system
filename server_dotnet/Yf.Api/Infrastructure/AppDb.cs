using Dapper;
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
        var builder = new MySqlConnectionStringBuilder(options.ConnectionString)
        {
            DateTimeKind = MySqlDateTimeKind.Utc,
            ConnectionTimeout = 10,
            DefaultCommandTimeout = 30,
            AllowUserVariables = false
        };
        DatabaseTransportPolicy.Validate(builder);
        var connection = new MySqlConnection(builder.ConnectionString);
        try { await connection.OpenAsync(cancellationToken); return connection; }
        catch { await connection.DisposeAsync(); throw; }
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

    public static async Task LockManagementAsync(MySqlConnection db, MySqlTransaction tx, CancellationToken ct = default)
    {
        var gate = await db.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT cfg_key FROM system_configs WHERE cfg_key='security.management_lock' FOR UPDATE", transaction: tx, cancellationToken: ct));
        if (gate is null) throw new InvalidOperationException("Management gate missing; database migration baseline required.");
    }

    public static async Task LockBusinessAsync(MySqlConnection db, MySqlTransaction tx, CancellationToken ct = default)
    {
        var gate = await db.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT cfg_key FROM system_configs WHERE cfg_key='security.management_lock' LOCK IN SHARE MODE", transaction: tx, cancellationToken: ct));
        if (gate is null) throw new InvalidOperationException("Management gate missing; database migration baseline required.");
    }

    public static async Task<CurrentUser> RecheckActorAsync(MySqlConnection db, MySqlTransaction tx, CurrentUser user, CancellationToken ct = default)
    {
        var row = await db.QuerySingleOrDefaultAsync<ActorRow>(new CommandDefinition(
            "SELECT id,employee_no AS EmployeeNo,user_type AS UserType,supplier_id AS SupplierId,status,must_change_password AS MustChangePassword FROM users WHERE id=@Id",
            new { user.Id }, tx, cancellationToken: ct));
        if (row is null || row.Status != "ACTIVE" || row.MustChangePassword) throw ApiException.Forbidden();
        if (row.UserType == "SUPPLIER" && (row.SupplierId is null || !await db.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM suppliers WHERE id=@Id AND status='ACTIVE')", new { Id = row.SupplierId }, tx, cancellationToken: ct))))
            throw ApiException.Forbidden();
        return new(row.Id, row.EmployeeNo, row.UserType, row.SupplierId);
    }

    public static async Task<CurrentUser> LockActorAsync(MySqlConnection db, MySqlTransaction tx, CurrentUser user, CancellationToken ct = default)
    {
        await LockBusinessAsync(db, tx, ct);
        return await RecheckActorAsync(db, tx, user, ct);
    }

    public static async Task<string[]> PermissionCodesAsync(MySqlConnection db, MySqlTransaction? tx, ulong userId, CancellationToken ct = default) =>
        (await db.QueryAsync<string>(new CommandDefinition("""
            SELECT DISTINCT p.code FROM permissions p
            JOIN role_permissions rp ON rp.permission_id=p.id
            JOIN user_roles ur ON ur.role_id=rp.role_id
            JOIN roles r ON r.id=ur.role_id AND r.status='ACTIVE' WHERE ur.user_id=@userId
            """, new { userId }, tx, cancellationToken: ct))).ToArray();

    public static async Task RequirePermissionAsync(MySqlConnection db, MySqlTransaction? tx, CurrentUser user, string permission, CancellationToken ct = default)
    {
        if (!(await PermissionCodesAsync(db, tx, user.Id, ct)).Contains(permission, StringComparer.Ordinal)) throw ApiException.Forbidden();
    }

    public static Task<bool> IsSystemAdminAsync(MySqlConnection db, MySqlTransaction? tx, ulong userId, CancellationToken ct = default) =>
        db.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS(SELECT 1 FROM user_roles ur JOIN roles r ON r.id=ur.role_id
                WHERE ur.user_id=@userId AND r.status='ACTIVE' AND r.is_built_in=1 AND r.name='系统管理员')
            """, new { userId }, tx, cancellationToken: ct));

    private sealed class ActorRow
    {
        public ulong Id { get; set; }
        public string EmployeeNo { get; set; } = "";
        public string UserType { get; set; } = "";
        public ulong? SupplierId { get; set; }
        public string Status { get; set; } = "";
        public bool MustChangePassword { get; set; }
    }
}
