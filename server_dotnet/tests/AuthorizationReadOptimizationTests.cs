using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MySqlConnector;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Identity;
using Yf.Api.Modules.Projects;

namespace Yf.Api.Tests;

[Collection(ConnectionLifecycleCollection.Name)]
public sealed class AuthorizationReadOptimizationTests
{
    [Fact]
    public async Task AppDbValidatesOnceAndEfContextsStayBoundToTheirOwnConnection()
    {
        Assert.Throws<InvalidOperationException>(() => new AppDb(new AppOptions()));

        await using var first = new MySqlConnection("Server=127.0.0.1;Database=first;SslMode=None");
        await using var second = new MySqlConnection("Server=127.0.0.1;Database=second;SslMode=None");
        await using (var firstContext = EfDb.Use(first))
        await using (var secondContext = EfDb.Use(second))
        {
            Assert.NotSame(firstContext, secondContext);
            Assert.Same(first, firstContext.Database.GetDbConnection());
            Assert.Same(second, secondContext.Database.GetDbConnection());
        }

        // Disposing a context must not make the cached, connection-bound options unusable or reuse
        // the disposed context instance.
        await using var next = EfDb.Use(first);
        Assert.Same(first, next.Database.GetDbConnection());
    }

    [Fact(Timeout = 120_000)]
    public async Task ReadAuthorizationDoesNotWaitForManagementGateAndRechecksLiveState()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await MigratedTestDatabase.CreateOrSkipAsync(ct);
        await SeedAsync(database, ct);
        var actor = new CurrentUser(9_200, "read-actor", "INTERNAL", null, "read-session");

        await using (var management = await database.Database.OpenAsync(ct))
        await using (var managementTx = await AppDb.BeginTransactionAsync(management, ct))
        {
            await AccessService.LockManagementAsync(management, managementTx, ct);
            var read = ReadProjectAsync(database, actor, ct);
            var completed = await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(3), ct));
            if (completed != read)
            {
                await managementTx.RollbackAsync(ct);
                await read;
                Assert.Fail("A pure authorization read waited for the exclusive management gate.");
            }
            Assert.Equal(9_400UL, (await read).Id);
            await managementTx.RollbackAsync(ct);
        }

        await using var connection = await database.Database.OpenAsync(ct);
        await using var tx = await AppDb.BeginTransactionAsync(connection, ct);
        var current = await AccessService.ReadActorAsync(connection, tx, actor, ct);
        await AccessService.RequirePermissionAsync(connection, tx, current, "project:list", ct);
        await ProjectAccessService.RequireViewForValidatedActorAsync(connection, tx, current, 9_400, false, ct);

        await database.ExecuteAsync("UPDATE projects SET responsible_user_id=9201 WHERE id=9400", null, ct);
        await AssertOutOfScopeAsync(() => ProjectAccessService.RequireViewForValidatedActorAsync(
            connection, tx, current, 9_400, false, ct));

        await database.ExecuteAsync("""
            UPDATE projects SET responsible_user_id=9200 WHERE id=9400;
            DELETE FROM role_permissions
            WHERE role_id=9100 AND permission_id=(SELECT id FROM permissions WHERE code='project:list');
            """, null, ct);
        await AssertForbiddenAsync(() => AccessService.RequirePermissionAsync(
            connection, tx, current, "project:list", ct));

        await database.ExecuteAsync("UPDATE users SET status='DISABLED' WHERE id=9200", null, ct);
        await AssertForbiddenAsync(() => AccessService.ReadActorAsync(connection, tx, actor, ct));
        await tx.RollbackAsync(ct);
    }

    [Fact(Timeout = 120_000)]
    public async Task GateProtectedPermissionReadsShareOneRealDatabaseQuery()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await MigratedTestDatabase.CreateOrSkipAsync(ct);
        await SeedAsync(database, ct);
        await using var connection = await database.Database.OpenAsync(ct);
        await using var tx = await AppDb.BeginTransactionAsync(connection, ct);
        await AccessService.LockBusinessAsync(connection, tx, ct);

        using (var counter = new EfCommandCounter(connection.Database))
        {
            Assert.True(await AccessService.HasPermissionAsync(connection, tx, 9_200, "project:list", ct));
            var (codes, menus) = await new PermissionService().GetCodesAndMenusAsync(connection, tx, 9_200, ct);
            Assert.Contains("project:list", codes);
            Assert.Contains("project:list", menus);
            Assert.True(await AccessService.HasPermissionAsync(connection, tx, 9_200, "project:list", ct));
            Assert.Equal(1, counter.Count);
        }
        await using (var context = EfDb.Use(connection, tx))
        {
            var permittedUsers = await AccessService.UsersWithPermission(context, "project:list").ToArrayAsync(ct);
            // The seeded system administrator also retains project:list.
            Assert.Equal(new ulong[] { 1UL, 9_200UL }, permittedUsers.Order().ToArray());
        }
        await tx.RollbackAsync(ct);
    }

    private static async Task<ProjectAccess> ReadProjectAsync(
        MigratedTestDatabase database, CurrentUser actor, CancellationToken ct)
    {
        await using var connection = await database.Database.OpenAsync(ct);
        return await ProjectAccessService.RequireViewAsync(connection, null, actor, 9_400, ct);
    }

    private static Task SeedAsync(MigratedTestDatabase database, CancellationToken ct) => database.ExecuteAsync("""
        INSERT INTO roles(id,name,status,is_built_in) VALUES(9100,'read role','ACTIVE',0);
        INSERT INTO role_permissions(role_id,permission_id)
            SELECT 9100,id FROM permissions WHERE code='project:list';
        INSERT INTO users(id,employee_no,password_hash,real_name,email,user_type,status,must_change_password)
            VALUES(9200,'read-actor','unused','Read Actor','','INTERNAL','ACTIVE',0),
                  (9201,'other-owner','unused','Other Owner','','INTERNAL','ACTIVE',0);
        INSERT INTO user_roles(user_id,role_id) VALUES(9200,9100);
        INSERT INTO refresh_tokens(user_id,session_id,token_hash,session_created_at,session_expires_at,expires_at,revoked,ip)
            VALUES(9200,'read-session',REPEAT('a',64),UTC_TIMESTAMP(),DATE_ADD(UTC_TIMESTAMP(),INTERVAL 30 DAY),
                   DATE_ADD(UTC_TIMESTAMP(),INTERVAL 1 DAY),0,'192.0.2.1');
        INSERT INTO suppliers(id,name,status,created_by) VALUES(9300,'read supplier','ACTIVE',1);
        INSERT INTO project_groups(id,name,supplier_id,status,created_by,responsible_user_id)
            VALUES(9350,'read group',9300,'IN_PROGRESS',1,9200);
        INSERT INTO projects(id,project_group_id,name,supplier_id,status,created_by,responsible_user_id)
            VALUES(9400,9350,'read project',9300,'IN_PROGRESS',1,9200);
        """, null, ct);

    private static async Task AssertForbiddenAsync(Func<Task> action)
    {
        var error = await Assert.ThrowsAsync<ApiException>(action);
        Assert.Equal(40301, error.Code);
    }

    private static async Task AssertOutOfScopeAsync(Func<Task> action)
    {
        var error = await Assert.ThrowsAsync<ApiException>(action);
        Assert.Equal(40302, error.Code);
    }

    private sealed class EfCommandCounter : IDisposable
    {
        private readonly List<IDisposable> subscriptions = [];
        private readonly IDisposable listeners;
        private int count;

        internal int Count => Volatile.Read(ref count);

        internal EfCommandCounter(string database)
        {
            listeners = DiagnosticListener.AllListeners.Subscribe(new Observer<DiagnosticListener>(listener =>
            {
                if (listener.Name != "Microsoft.EntityFrameworkCore") return;
                subscriptions.Add(listener.Subscribe(new Observer<KeyValuePair<string, object?>>(item =>
                {
                    if (item.Key.EndsWith("CommandExecuting", StringComparison.Ordinal)
                        && item.Value is CommandEventData data
                        && data.Command.Connection?.Database == database)
                        Interlocked.Increment(ref count);
                })));
            }));
        }

        public void Dispose()
        {
            listeners.Dispose();
            foreach (var subscription in subscriptions) subscription.Dispose();
        }
    }

    private sealed class Observer<T>(Action<T> action) : IObserver<T>
    {
        public void OnNext(T value) => action(value);
        public void OnCompleted() { }
        public void OnError(Exception error) { }
    }
}
