using Dapper;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Projects;

namespace Yf.Api.Tests;

/// <summary>
/// Business writes re-read the actor's permissions inside their own transaction, after taking the shared
/// business gate. A permission revoked while the write waits for the gate (an administrator holds it exclusively
/// for the revocation) must therefore reject the write with no side effects. Mirrors the login/password-change
/// cases in LoginThrottleTests; the upload-merge case lives in FilesRecoveryTests next to its fixture.
/// </summary>
public sealed class WritePermissionRecheckTests
{
    private const string DefaultForbiddenMessage = "无操作权限";

    [Fact(Timeout = 120_000)]
    public async Task MaterialApprovalIsRejectedWhenTheOwnerLosesProjectListWhileItWaitsForTheGate()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await MigratedTestDatabase.CreateOrSkipAsync(ct);
        await SeedAsync(database, ct);
        var audit = new AuditService([]);
        var projects = new ProjectService(audit, new AppOptions());
        var owner = new CurrentUser(101, "recheck-owner", "INTERNAL", null);

        await using var conn = await database.Database.OpenAsync(ct);
        Assert.True(await AccessService.HasPermissionAsync(conn, null, owner.Id, "project:list", ct));

        var denied = await RevokeWhileWaitingAsync(database, 9001, "project:list",
            () => projects.ApproveMaterialsAsync(conn, owner, 1001,
                new ProjectMaterialReviewRequest { ExpectedSupplierFileId = 1 }, null, ct), ct);

        Assert.Equal((403, 40301, DefaultForbiddenMessage), (denied.Status, denied.Code, denied.Message));
        Assert.False(await AccessService.HasPermissionAsync(conn, null, owner.Id, "project:list", ct));
        Assert.Equal(ProjectStatuses.InProgress, await conn.ExecuteScalarAsync<string>(
            new CommandDefinition("SELECT status FROM projects WHERE id=1001", cancellationToken: ct)));
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM audit_logs WHERE action LIKE 'PROJECT_MATERIAL_%'", cancellationToken: ct)));
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM email_outbox WHERE event_type LIKE 'PROJECT_MATERIAL_%'", cancellationToken: ct)));
    }

    [Fact(Timeout = 120_000)]
    public async Task MessageSendIsRejectedWhenTheSenderLosesMessageCreateWhileItWaitsForTheGate()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await MigratedTestDatabase.CreateOrSkipAsync(ct);
        await SeedAsync(database, ct);
        var messages = new MessageService(new AuditService([]), new AppOptions());
        var supplier = new CurrentUser(201, "recheck-supplier", "SUPPLIER", 100);

        await using var conn = await database.Database.OpenAsync(ct);
        // Control: with the permission in place the very same call succeeds.
        await messages.CreateAsync(conn, supplier, 1001, new MessageCreateRequest { Content = "撤销权限前的留言", SignerName = "测试签名" }, null, ct);
        Assert.Equal(1, await MessageCountAsync(conn, ct));

        var denied = await RevokeWhileWaitingAsync(database, 9002, "message:create",
            () => messages.CreateAsync(conn, supplier, 1001, new MessageCreateRequest { Content = "撤销权限后的留言", SignerName = "测试签名" }, null, ct), ct);

        Assert.Equal((403, 40301, DefaultForbiddenMessage), (denied.Status, denied.Code, denied.Message));
        Assert.Equal(1, await MessageCountAsync(conn, ct));
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM messages WHERE content='撤销权限后的留言'", cancellationToken: ct)));
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM audit_logs WHERE action='MESSAGE_CREATE'", cancellationToken: ct)));
    }

    /// <summary>
    /// Holds the exclusive management gate (as a permission change does), starts the write, waits until it is blocked
    /// on the gate, revokes the permission through the gate holder and releases it. Returns the write's rejection.
    /// </summary>
    private static async Task<ApiException> RevokeWhileWaitingAsync<T>(
        MigratedTestDatabase database, ulong roleId, string permission, Func<Task<T>> write, CancellationToken ct)
    {
        await using var management = await database.Database.OpenAsync(ct);
        await using var managementTx = await AppDb.BeginTransactionAsync(management, ct);
        await AccessService.LockManagementAsync(management, managementTx, ct);
        var pending = Task.Run(write, ct);
        try
        {
            await LockWaitProbe.WaitForLockWaitAsync(database.Options.ConnectionString, ct, pending);
            Assert.False(pending.IsCompleted, "The write must be waiting for the business gate.");
            Assert.Equal(1, await management.ExecuteAsync(new CommandDefinition(
                "DELETE FROM role_permissions WHERE role_id=@roleId AND permission_id=(SELECT id FROM permissions WHERE code=@permission)",
                new { roleId, permission }, managementTx, cancellationToken: ct)));
            await managementTx.CommitAsync(ct);
        }
        catch
        {
            await managementTx.RollbackAsync(CancellationToken.None);
            try { await pending; } catch { }
            throw;
        }
        return await Assert.ThrowsAsync<ApiException>(() => pending);
    }

    private static Task<int> MessageCountAsync(MySqlConnector.MySqlConnection conn, CancellationToken ct) =>
        conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM messages WHERE sender_id=201 AND status='NORMAL'", cancellationToken: ct));

    private static Task SeedAsync(MigratedTestDatabase database, CancellationToken ct) => database.ExecuteAsync("""
        UPDATE users SET must_change_password=0 WHERE id=1;
        INSERT INTO suppliers(id,name,status,created_by) VALUES(100,'复核供应商','ACTIVE',1);
        INSERT INTO roles(id,name,description,is_built_in,status)
        VALUES(9001,'复核项目负责人','复核测试角色',0,'ACTIVE'),(9002,'复核供应商人员','复核测试角色',0,'ACTIVE');
        INSERT INTO role_permissions(role_id,permission_id)
        SELECT r.role_id,p.id
        FROM (SELECT 9001 AS role_id UNION ALL SELECT 9002) r
        CROSS JOIN permissions p
        WHERE p.code IN ('project:list','message:create','file:upload');
        INSERT INTO users(id,employee_no,password_hash,real_name,email,user_type,supplier_id,status,must_change_password)
        VALUES(101,'recheck-owner','unused','项目负责人','','INTERNAL',NULL,'ACTIVE',0),
              (201,'recheck-supplier','unused','厂商人员','','SUPPLIER',100,'ACTIVE',0);
        INSERT INTO user_roles(user_id,role_id) VALUES(101,9001),(201,9002);
        INSERT INTO projects(id,name,supplier_id,status,created_by,responsible_user_id)
        VALUES(1001,'复核项目',100,'IN_PROGRESS',1,101);
        INSERT INTO files(id,project_id,uploader_id,direction,original_name,stored_name,ext,size_bytes,storage_path,status)
        VALUES(1,1001,201,'S2C','a.txt','recheck-a.txt','txt',1,'files/recheck-a.txt','AVAILABLE');
        """, null, ct);
}
