using Dapper;
using Microsoft.EntityFrameworkCore;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Identity;

namespace Yf.Api.Tests;

[Collection(ConnectionLifecycleCollection.Name)]
public sealed class IdentityRevisionDatabaseTests
{
    [Fact(Timeout = 120_000)]
    public async Task SecurityTableUpdatesInvalidateCachedProjection()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await MigratedTestDatabase.CreateOrSkipAsync(ct);
        await SeedSupplierSessionAsync(database, ct);
        var cache = new IdentityProjectionCache();
        var claims = Claims();

        var initial = await cache.ResolveAsync(database.Database, claims, ct);
        Assert.Equal("revision-user", initial!.EmployeeNo);
        Assert.True(initial.SupplierActive);

        await database.ExecuteAsync("UPDATE users SET employee_no='revision-user-updated' WHERE id=99200", null, ct);
        var changedUser = await cache.ResolveAsync(database.Database, claims, ct);
        Assert.Equal("revision-user-updated", changedUser!.EmployeeNo);
        Assert.NotSame(initial, changedUser);

        await database.ExecuteAsync("UPDATE suppliers SET status='DISABLED' WHERE id=99300", null, ct);
        var disabledSupplier = await cache.ResolveAsync(database.Database, claims, ct);
        Assert.False(disabledSupplier!.SupplierActive);

        await database.ExecuteAsync("UPDATE suppliers SET status='ACTIVE' WHERE id=99300", null, ct);
        Assert.True((await cache.ResolveAsync(database.Database, claims, ct))!.SupplierActive);

        await database.ExecuteAsync("UPDATE refresh_tokens SET revoked=1 WHERE user_id=99200", null, ct);
        var revoked = await cache.ResolveAsync(database.Database, claims, ct);
        Assert.False(revoked!.SessionActive);
    }

    [Fact(Timeout = 120_000)]
    public async Task RolledBackSecurityMutationDoesNotAdvanceCommittedRevisionOrReplaceCache()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await MigratedTestDatabase.CreateOrSkipAsync(ct);
        await SeedSupplierSessionAsync(database, ct);
        var cache = new IdentityProjectionCache();
        var claims = Claims();
        var cached = await cache.ResolveAsync(database.Database, claims, ct);
        var before = await RevisionAsync(database, ct);

        await using (var connection = await database.Database.OpenAsync(ct))
        await using (var transaction = await AppDb.BeginTransactionAsync(connection, ct))
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE users SET employee_no='must-rollback' WHERE id=99200", transaction: transaction, cancellationToken: ct));
            var inside = await connection.ExecuteScalarAsync<ulong>(new CommandDefinition(
                "SELECT CAST(cfg_value AS UNSIGNED) FROM system_configs WHERE cfg_key='security.identity_revision'",
                transaction: transaction, cancellationToken: ct));
            Assert.True(inside > before);
            await transaction.RollbackAsync(ct);
        }

        Assert.Equal(before, await RevisionAsync(database, ct));
        var after = await cache.ResolveAsync(database.Database, claims, ct);
        Assert.Same(cached, after);
        Assert.Equal("revision-user", after!.EmployeeNo);
    }

    [Fact(Timeout = 120_000)]
    public async Task DatabaseClockExpiresCachedSessionWithoutRevisionChange()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await MigratedTestDatabase.CreateOrSkipAsync(ct);
        await SeedSupplierSessionAsync(database, ct);
        await database.ExecuteAsync("""
            UPDATE refresh_tokens
            SET expires_at=DATE_ADD(UTC_TIMESTAMP(),INTERVAL 3 SECOND),
                session_expires_at=DATE_ADD(UTC_TIMESTAMP(),INTERVAL 1 DAY)
            WHERE user_id=99200
            """, null, ct);
        var cache = new IdentityProjectionCache();
        var claims = Claims();
        Assert.True((await cache.ResolveAsync(database.Database, claims, ct))!.SessionActive);
        var revision = await RevisionAsync(database, ct);

        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            while (await database.ScalarAsync<int>(
                       "SELECT expires_at <= UTC_TIMESTAMP(6) FROM refresh_tokens WHERE user_id=99200", deadline.Token) == 0)
                await Task.Delay(100, deadline.Token);
        }

        Assert.Equal(revision, await RevisionAsync(database, ct));
        Assert.False((await cache.ResolveAsync(database.Database, claims, ct))!.SessionActive);
    }

    [Fact(Timeout = 120_000)]
    public async Task RuntimeSeedValidationRequiresRevisionAndEveryIdentityTrigger()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await MigratedTestDatabase.CreateOrSkipAsync(ct);
        var factory = EfTestSupport.DbContextFactory(database.Options);
        await using (var valid = await factory.CreateDbContextAsync(ct))
            await BootstrapSeedCatalog.ValidateRuntimeSeedAsync(valid, ct);

        await database.ExecuteAsync("DELETE FROM system_configs WHERE cfg_key='security.identity_revision'", null, ct);
        await using (var missingRevision = await factory.CreateDbContextAsync(ct))
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                BootstrapSeedCatalog.ValidateRuntimeSeedAsync(missingRevision, ct));

        await database.ExecuteAsync("""
            INSERT INTO system_configs(cfg_key,cfg_value,description,updated_at)
            VALUES('security.identity_revision','0','test restoration',UTC_TIMESTAMP(3));
            DROP TRIGGER trg_identity_users_update;
            """, null, ct);
        await using var missingTrigger = await factory.CreateDbContextAsync(ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BootstrapSeedCatalog.ValidateRuntimeSeedAsync(missingTrigger, ct));
    }

    private static AccessClaims Claims() => new(
        99_200, "signed-employee-no", "revision-session", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds());

    private static Task<ulong> RevisionAsync(MigratedTestDatabase database, CancellationToken ct) =>
        database.ScalarAsync<ulong>("SELECT CAST(cfg_value AS UNSIGNED) FROM system_configs WHERE cfg_key='security.identity_revision'", ct);

    private static Task SeedSupplierSessionAsync(MigratedTestDatabase database, CancellationToken ct) => database.ExecuteAsync("""
        INSERT INTO suppliers(id,name,status,created_by) VALUES(99300,'Revision Supplier','ACTIVE',1);
        INSERT INTO users(id,employee_no,password_hash,real_name,email,user_type,supplier_id,status,must_change_password,created_by)
            VALUES(99200,'revision-user','unused','Revision User','','SUPPLIER',99300,'ACTIVE',0,1);
        INSERT INTO refresh_tokens(user_id,session_id,token_hash,session_created_at,session_expires_at,expires_at,revoked,ip)
            VALUES(99200,'revision-session',REPEAT('b',64),UTC_TIMESTAMP(),DATE_ADD(UTC_TIMESTAMP(),INTERVAL 30 DAY),
                   DATE_ADD(UTC_TIMESTAMP(),INTERVAL 1 DAY),0,'192.0.2.2');
        """, null, ct);
}
