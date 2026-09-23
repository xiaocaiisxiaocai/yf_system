using Microsoft.Extensions.Logging.Abstractions;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Identity;
using Yf.Api.Modules.SystemManagement;

namespace Yf.Api.Tests;

// Migrated databases set YF_BOOTSTRAP_PASSWORD process-wide, like the other initialization tests.
[Collection(ConnectionLifecycleCollection.Name)]
public sealed class MaintenanceWorkerTests
{
    [Fact(Timeout = 120_000)]
    public async Task AuditRetentionDeletesOnlyExpiredRowsInBatchesAndRecordsASummary()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await MigratedTestDatabase.CreateOrSkipAsync(ct);
        await database.ExecuteAsync("DELETE FROM audit_logs", null, ct);
        // More expired rows than one delete batch, plus rows just inside the window.
        var expired = AuditRetentionService.BatchSize + 5;
        for (var offset = 0; offset < expired; offset += 500)
        {
            var count = Math.Min(500, expired - offset);
            var values = string.Join(",", Enumerable.Range(0, count).Select(_ =>
                "('LOGIN',NULL,NULL,'{}',DATE_SUB(UTC_TIMESTAMP(6), INTERVAL 31 DAY))"));
            await database.ExecuteAsync(
                "INSERT INTO audit_logs(action,target_type,target_id,detail,created_at) VALUES " + values, null, ct);
        }
        await database.ExecuteAsync("""
            INSERT INTO audit_logs(action,target_type,target_id,detail,created_at) VALUES
            ('LOGIN',NULL,NULL,'{}',DATE_SUB(UTC_TIMESTAMP(6), INTERVAL 29 DAY)),
            ('PROJECT_UPDATE','project','1','{}',UTC_TIMESTAMP(6))
            """, null, ct);
        var service = new AuditRetentionService(database.Database, database.Options, new AuditService([]),
            NullLogger<AuditRetentionService>.Instance);

        Assert.Equal(30, database.Options.AuditRetentionDays);
        Assert.Equal(expired, await service.PurgeAsync(ct));

        Assert.Equal(0L, await database.ScalarAsync<long>(
            "SELECT COUNT(*) FROM audit_logs WHERE created_at < DATE_SUB(UTC_TIMESTAMP(6), INTERVAL 30 DAY)", ct));
        Assert.Equal(2L, await database.ScalarAsync<long>(
            "SELECT COUNT(*) FROM audit_logs WHERE action IN ('LOGIN','PROJECT_UPDATE')", ct));
        Assert.Equal((long)expired, await database.ScalarAsync<long>(
            $"SELECT CAST(JSON_EXTRACT(detail,'$.deleted') AS SIGNED) FROM audit_logs WHERE action='{AuditRetentionService.Action}'", ct));

        // Nothing left to purge: no further summary rows.
        Assert.Equal(0, await service.PurgeAsync(ct));
        Assert.Equal(1L, await database.ScalarAsync<long>(
            $"SELECT COUNT(*) FROM audit_logs WHERE action='{AuditRetentionService.Action}'", ct));
    }

    [Fact(Timeout = 120_000)]
    public async Task SessionCleanupKeepsRotatedTokensUntilTheFamilyCannotBeReplayed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await MigratedTestDatabase.CreateOrSkipAsync(ct);
        await database.ExecuteAsync("""
            INSERT INTO refresh_tokens(
              user_id,session_id,token_hash,session_created_at,session_expires_at,expires_at,revoked) VALUES
            (1,'old-family',REPEAT('a',64),DATE_SUB(UTC_TIMESTAMP(), INTERVAL 38 DAY),DATE_SUB(UTC_TIMESTAMP(), INTERVAL 8 DAY),DATE_SUB(UTC_TIMESTAMP(), INTERVAL 31 DAY),1),
            (1,'grace-family',REPEAT('b',64),DATE_SUB(UTC_TIMESTAMP(), INTERVAL 36 DAY),DATE_SUB(UTC_TIMESTAMP(), INTERVAL 6 DAY),DATE_SUB(UTC_TIMESTAMP(), INTERVAL 29 DAY),1),
            (1,'live-family-old-token',REPEAT('c',64),DATE_SUB(UTC_TIMESTAMP(), INTERVAL 20 DAY),DATE_ADD(UTC_TIMESTAMP(), INTERVAL 10 DAY),DATE_SUB(UTC_TIMESTAMP(), INTERVAL 13 DAY),1),
            (1,'live-family-current-token',REPEAT('d',64),DATE_SUB(UTC_TIMESTAMP(), INTERVAL 20 DAY),DATE_ADD(UTC_TIMESTAMP(), INTERVAL 10 DAY),DATE_ADD(UTC_TIMESTAMP(), INTERVAL 1 DAY),0)
            """, null, ct);
        var service = new SessionCleanupService(database.Database, database.Options,
            NullLogger<SessionCleanupService>.Instance);

        Assert.Equal(1, await service.PurgeExpiredSessionsAsync(ct));
        Assert.Equal("grace-family,live-family-current-token,live-family-old-token", await database.ScalarAsync<string>(
            "SELECT GROUP_CONCAT(session_id ORDER BY session_id) FROM refresh_tokens", ct));
    }
}
