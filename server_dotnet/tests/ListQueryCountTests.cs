using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Admin;
using Yf.Api.Modules.Identity;

namespace Yf.Api.Tests;

/// <summary>Admin lists must cost a fixed number of queries per page, not a few per row (N+1).</summary>
[Collection(ConnectionLifecycleCollectionDefinition.Name)]
public sealed class ListQueryCountTests
{
    [Fact(Timeout = 120_000)]
    public async Task UserRoleAndSupplierAccountListsDoNotQueryPerRow()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await MigratedTestDatabase.CreateOrSkipAsync(ct);
        await database.ExecuteAsync("""
            INSERT INTO departments(id,name,parent_id,kind,sort_no,status) VALUES(900,'计数课',NULL,'SECTION',0,'ACTIVE');
            INSERT INTO suppliers(id,name,status,created_by) VALUES(700,'少账号供应商','ACTIVE',1),(701,'多账号供应商','ACTIVE',1);
            """, null, ct);
        var permissionIds = new[] { "project:list", "file:upload", "file:download" };
        for (var i = 0; i < 24; i++)
        {
            await database.ExecuteAsync("""
                INSERT INTO roles(id,name,description,is_built_in,status) VALUES(@roleId,@roleName,'计数',0,'ACTIVE');
                INSERT INTO role_permissions(role_id,permission_id) SELECT @roleId,id FROM permissions WHERE code IN @permissionIds;
                INSERT INTO users(id,employee_no,password_hash,real_name,email,user_type,supplier_id,department_id,status,must_change_password)
                VALUES(@userId,@employeeNo,'unused',@employeeNo,'',@userType,@supplierId,@departmentId,'ACTIVE',0);
                INSERT INTO user_roles(user_id,role_id) VALUES(@userId,@roleId);
                """, new
                {
                    roleId = 5000 + i, roleName = "计数角色" + i, permissionIds,
                    userId = 6000 + i, employeeNo = "count" + i,
                    // Users 0-11 are internal; 12-13 belong to the small supplier, 14-23 to the large one.
                    userType = i < 12 ? "INTERNAL" : "SUPPLIER",
                    supplierId = i < 12 ? (ulong?)null : i < 14 ? 700UL : 701UL,
                    departmentId = i < 12 ? 900UL : (ulong?)null,
                }, ct);
        }

        var counter = new CommandCounter();
        var factory = CountingFactory(database.Options, counter);
        var admin = new CurrentUser(1, "admin", "INTERNAL", null);
        var users = new UserService(factory, new PermissionService(), new AuditService([]));
        var roles = new RoleService(factory, new PermissionService(), new AuditService([]));
        var suppliers = new SupplierService(factory, new PermissionService(), new AuditService([]));

        async Task<int> CountAsync(Func<Task> action)
        {
            counter.Reset();
            await action();
            return counter.Count;
        }

        var fewUsers = await CountAsync(async () => Assert.Equal(3, (await users.ListAsync(admin, 1, 3, 0, null, 900, null, ct)).List.Count));
        var manyUsers = await CountAsync(async () => Assert.Equal(12, (await users.ListAsync(admin, 1, 20, 0, null, 900, null, ct)).List.Count));
        Assert.Equal(fewUsers, manyUsers);

        var fewRoles = await CountAsync(async () => Assert.Equal(3, (await roles.ListAsync(admin, 1, 3, 0, ct)).List.Count));
        var manyRoles = await CountAsync(async () => Assert.Equal(20, (await roles.ListAsync(admin, 1, 20, 0, ct)).List.Count));
        Assert.Equal(fewRoles, manyRoles);

        var fewAccounts = await CountAsync(async () => Assert.Equal(2, (await suppliers.AccountsAsync(admin, 700, ct)).Length));
        var manyAccounts = await CountAsync(async () => Assert.Equal(10, (await suppliers.AccountsAsync(admin, 701, ct)).Length));
        Assert.Equal(fewAccounts, manyAccounts);

        // Batched rows still carry the per-row details.
        var listed = (await users.ListAsync(admin, 1, 20, 0, "count3", 900, null, ct)).List.Single();
        Assert.Equal("计数课", listed.DepartmentName);
        Assert.Equal(["计数角色3"], listed.RoleNames);
        var role = (await roles.ListAsync(admin, 1, 100, 0, ct)).List.Single(item => item.Id == 5014);
        Assert.Equal(3, role.PermissionIds.Count);
        Assert.Equal(1UL, role.AssignedUserCount);
        Assert.True(role.SupplierRestricted);
        Assert.All(await suppliers.AccountsAsync(admin, 701, ct), account => Assert.StartsWith("计数角色", account.RoleName));
    }

    private static Factory CountingFactory(AppOptions options, CommandCounter counter)
    {
        var connectionString = AppDb.BuildConnectionString(options);
        var contextOptions = new DbContextOptionsBuilder<YfDbContext>()
            .UseMySql(connectionString, ServerVersion.AutoDetect(connectionString))
            .AddInterceptors(counter)
            .Options;
        return new Factory(contextOptions);
    }

    private sealed class Factory(DbContextOptions<YfDbContext> options) : IDbContextFactory<YfDbContext>
    {
        public YfDbContext CreateDbContext() => new(options);
    }

    private sealed class CommandCounter : DbCommandInterceptor
    {
        private int count;
        public int Count => Volatile.Read(ref count);
        public void Reset() => Interlocked.Exchange(ref count, 0);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref count);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref count);
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
