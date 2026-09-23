using System.Text.Json;
using Dapper;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.SystemManagement;

namespace Yf.Api.Tests;

[Collection(ConnectionLifecycleCollection.Name)]
public sealed class AuditDeletionSecurityTests
{
    [Fact]
    public async Task RecentSecurityLogsAndCleanupReceiptsCannotBeDeletedEvenByAdministrator()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await MigratedTestDatabase.CreateOrSkipAsync(ct);
        await fixture.ExecuteAsync("UPDATE users SET must_change_password=0 WHERE id=1", null, ct);
        await using var conn = await fixture.Database.OpenAsync(ct);
        await conn.ExecuteAsync("""
            INSERT INTO audit_logs(id,user_id,employee_no,action,created_at) VALUES
            (91001,1,'admin','LOGIN_FAILED',UTC_TIMESTAMP()),
            (91002,1,'admin','FILE_DOWNLOAD',DATE_SUB(UTC_TIMESTAMP(),INTERVAL 31 DAY)),
            (91003,1,'admin','AUDIT_LOG_RETENTION',DATE_SUB(UTC_TIMESTAMP(),INTERVAL 40 DAY)),
            (91004,1,'admin','AUDIT_LOG_DELETE',DATE_SUB(UTC_TIMESTAMP(),INTERVAL 40 DAY));
            """);
        var service = new SystemService(fixture.Database, new AuditService([]), fixture.Options);
        var actor = new CurrentUser(1, "admin", "INTERNAL", null);
        foreach (var ids in new ulong[][] { [91001, 91002], [91003], [91004] })
            Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => service.DeleteLogsAsync(ids, actor, ct))).Status);
        Assert.Equal(4, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM audit_logs WHERE id BETWEEN 91001 AND 91004"));
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM audit_logs WHERE action='AUDIT_LOG_DELETE'"));
    }

    [Fact]
    public async Task ExpiredDeletionPreservesOriginalActionActorTimeAndTargetWithoutCopyingRawDetails()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await MigratedTestDatabase.CreateOrSkipAsync(ct);
        await fixture.ExecuteAsync("UPDATE users SET must_change_password=0 WHERE id=1", null, ct);
        await using var conn = await fixture.Database.OpenAsync(ct);
        await conn.ExecuteAsync("""
            INSERT INTO audit_logs(id,user_id,employee_no,action,target_type,target_id,detail,created_at)
            VALUES(92001,1,'historic-actor','FILE_DOWNLOAD','file','42',
            '{"privateDiagnostic":"do-not-copy-original-details"}',DATE_SUB(UTC_TIMESTAMP(),INTERVAL 31 DAY));
            """);
        var service = new SystemService(fixture.Database, new AuditService([]), fixture.Options);
        var response = await service.DeleteLogsAsync([92001, 92001], new CurrentUser(1, "admin", "INTERNAL", null), ct);
        Assert.Equal(1, response.Deleted);
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM audit_logs WHERE id=92001"));
        var detail = await conn.ExecuteScalarAsync<string>("SELECT detail FROM audit_logs WHERE action='AUDIT_LOG_DELETE' ORDER BY id DESC LIMIT 1");
        Assert.DoesNotContain("do-not-copy-original-details", detail!);
        using var parsed = JsonDocument.Parse(detail!);
        var original = Assert.Single(parsed.RootElement.GetProperty("deletedRecords").EnumerateArray());
        Assert.Equal(92001UL, original.GetProperty("id").GetUInt64());
        Assert.Equal("FILE_DOWNLOAD", original.GetProperty("action").GetString());
        Assert.Equal(1UL, original.GetProperty("actorId").GetUInt64());
        Assert.Equal("historic-actor", original.GetProperty("employeeNo").GetString());
        Assert.Equal("42", original.GetProperty("targetId").GetString());
        Assert.True(original.GetProperty("createdAt").GetDateTime() < DateTime.UtcNow.AddDays(-30));
    }
}
