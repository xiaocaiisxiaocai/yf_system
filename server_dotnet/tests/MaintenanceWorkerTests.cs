using Microsoft.Extensions.Logging.Abstractions;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Identity;
using Yf.Api.Modules.SystemManagement;

namespace Yf.Api.Tests;

// Keep maintenance and initialization fixtures isolated from process-wide configuration tests.
[Collection(ConnectionLifecycleCollectionDefinition.Name)]
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
        await database.ExecuteAsync("""
            INSERT INTO refresh_tokens(
              user_id,session_id,token_hash,session_created_at,session_expires_at,expires_at,revoked)
            SELECT 1,CONCAT('batch-',p1.id,'-',p2.id,'-',p3.id),
                   SHA2(CONCAT('batch-',p1.id,'-',p2.id,'-',p3.id),256),
                   DATE_SUB(UTC_TIMESTAMP(), INTERVAL 38 DAY),DATE_SUB(UTC_TIMESTAMP(), INTERVAL 8 DAY),
                   DATE_SUB(UTC_TIMESTAMP(), INTERVAL 31 DAY),1
            FROM permissions p1 CROSS JOIN permissions p2 CROSS JOIN permissions p3
            LIMIT 1001
            """, null, ct);
        var service = new SessionCleanupService(database.Database, database.Options,
            NullLogger<SessionCleanupService>.Instance);

        Assert.Equal(1000, SessionCleanupService.BatchSize);
        Assert.Equal(1002, await service.PurgeExpiredSessionsAsync(ct));
        Assert.Equal("grace-family,live-family-current-token,live-family-old-token", await database.ScalarAsync<string>(
            "SELECT GROUP_CONCAT(session_id ORDER BY session_id) FROM refresh_tokens", ct));
    }

    [Fact(Timeout = 120_000)]
    public async Task OperationalRetentionRemovesOnlyOldTerminalUploadSessionsAndTheirDirectories()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await MigratedTestDatabase.CreateOrSkipAsync(ct);
        var storage = Path.Combine(Path.GetTempPath(), "yf-operational-retention-" + Guid.NewGuid().ToString("N"));
        database.Options.StorageRoot = storage;
        database.Options.UploadSessionRetentionDays = 90;
        var oldCompleted = Guid.NewGuid().ToString("D");
        var oldAborted = Guid.NewGuid().ToString("D");
        var oldActive = Guid.NewGuid().ToString("D");
        var recentExpired = Guid.NewGuid().ToString("D");
        var unsafeTerminal = Guid.NewGuid().ToString("D");
        var ids = new[] { oldCompleted, oldAborted, oldActive, recentExpired };
        var directories = ids.ToDictionary(id => id, id => Path.Combine(storage, "tmp", id));
        foreach (var directory in directories.Values)
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "leftover.part"), "test", ct);
        }
        var protectedDirectory = Path.Combine(storage, "business-data");
        Directory.CreateDirectory(protectedDirectory);
        await File.WriteAllTextAsync(Path.Combine(protectedDirectory, "must-survive.txt"), "keep", ct);

        try
        {
            await database.ExecuteAsync("""
                INSERT INTO suppliers(id,name,status,created_by) VALUES(9800,'retention supplier','ACTIVE',1);
                INSERT INTO project_groups(id,name,supplier_id,status,created_by,responsible_user_id)
                VALUES(9800,'retention group',9800,'IN_PROGRESS',1,1);
                INSERT INTO projects(id,project_group_id,name,supplier_id,status,created_by,responsible_user_id)
                VALUES(9800,9800,'retention project',9800,'IN_PROGRESS',1,1);
                INSERT INTO upload_sessions(
                    id,project_id,uploader_id,file_name,file_size,file_fingerprint,chunk_size,total_chunks,temp_dir,
                    status,expires_at,created_at,updated_at) VALUES
                (@OldCompleted,9800,1,'a.bin',1,REPEAT('a',64),1,1,@OldCompletedDir,'COMPLETED',UTC_TIMESTAMP(3),DATE_SUB(UTC_TIMESTAMP(3),INTERVAL 100 DAY),DATE_SUB(UTC_TIMESTAMP(3),INTERVAL 100 DAY)),
                (@OldAborted,9800,1,'b.bin',1,REPEAT('b',64),1,1,@OldAbortedDir,'ABORTED',UTC_TIMESTAMP(3),DATE_SUB(UTC_TIMESTAMP(3),INTERVAL 100 DAY),DATE_SUB(UTC_TIMESTAMP(3),INTERVAL 100 DAY)),
                (@OldActive,9800,1,'c.bin',1,REPEAT('c',64),1,1,@OldActiveDir,'UPLOADING',UTC_TIMESTAMP(3),DATE_SUB(UTC_TIMESTAMP(3),INTERVAL 100 DAY),DATE_SUB(UTC_TIMESTAMP(3),INTERVAL 100 DAY)),
                (@RecentExpired,9800,1,'d.bin',1,REPEAT('d',64),1,1,@RecentExpiredDir,'EXPIRED',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
                (@UnsafeTerminal,9800,1,'e.bin',1,REPEAT('e',64),1,1,@ProtectedDir,'EXPIRED',UTC_TIMESTAMP(3),DATE_SUB(UTC_TIMESTAMP(3),INTERVAL 100 DAY),DATE_SUB(UTC_TIMESTAMP(3),INTERVAL 100 DAY))
                """, new
            {
                OldCompleted = oldCompleted,
                OldAborted = oldAborted,
                OldActive = oldActive,
                RecentExpired = recentExpired,
                UnsafeTerminal = unsafeTerminal,
                OldCompletedDir = directories[oldCompleted],
                OldAbortedDir = directories[oldAborted],
                OldActiveDir = directories[oldActive],
                RecentExpiredDir = directories[recentExpired],
                ProtectedDir = protectedDirectory,
            }, ct);
            var service = new OperationalRetentionService(database.Database, database.Options,
                NullLogger<OperationalRetentionService>.Instance);

            Assert.Equal(2, await service.PurgeUploadSessionsAsync(ct));
            Assert.False(Directory.Exists(directories[oldCompleted]));
            Assert.False(Directory.Exists(directories[oldAborted]));
            Assert.True(Directory.Exists(directories[oldActive]));
            Assert.True(Directory.Exists(directories[recentExpired]));
            Assert.True(File.Exists(Path.Combine(protectedDirectory, "must-survive.txt")));
            Assert.Equal(string.Join(',', new[] { oldActive, recentExpired, unsafeTerminal }.Order(StringComparer.Ordinal)), await database.ScalarAsync<string>(
                "SELECT GROUP_CONCAT(id ORDER BY id) FROM upload_sessions", ct));
        }
        finally
        {
            if (Directory.Exists(storage)) Directory.Delete(storage, recursive: true);
        }
    }
}
