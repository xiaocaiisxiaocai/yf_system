using Microsoft.AspNetCore.Http;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.SystemManagement;

namespace Yf.Api.Tests;

[Collection(ConnectionLifecycleCollectionDefinition.Name)]
public sealed class DataIntegrityTests
{
    [Fact(Timeout = 120_000)]
    public async Task SchemaUsesConsistentPrecisionCollationAndRelationships()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await MigratedTestDatabase.CreateOrSkipAsync(ct);

        Assert.Equal(3L, await database.ScalarAsync<long>("""
            SELECT COUNT(*) FROM information_schema.tables
            WHERE table_schema=DATABASE()
              AND table_name IN ('project_status_logs','project_activities','project_copy_worker_state')
              AND table_collation='utf8mb4_unicode_ci'
            """, ct));
        Assert.Equal(8L, await database.ScalarAsync<long>("""
            SELECT COUNT(*) FROM information_schema.columns
            WHERE table_schema=DATABASE() AND datetime_precision=3 AND (
              (table_name='audit_logs' AND column_name='created_at') OR
              (table_name='messages' AND column_name IN ('created_at','deleted_at')) OR
              (table_name='projects' AND column_name IN ('created_at','updated_at')) OR
              (table_name='files' AND column_name='created_at') OR
              (table_name='email_outbox' AND column_name='created_at') OR
              (table_name='message_reads' AND column_name='read_at'))
            """, ct));
        Assert.Equal(3L, await database.ScalarAsync<long>("""
            SELECT COUNT(*) FROM information_schema.referential_constraints
            WHERE constraint_schema=DATABASE() AND (
              (constraint_name='fk_users_department' AND delete_rule='SET NULL') OR
              (constraint_name='fk_users_created_by' AND delete_rule='SET NULL') OR
              (constraint_name='fk_mr_msg' AND delete_rule='CASCADE'))
            """, ct));
        Assert.Equal(1L, await database.ScalarAsync<long>("""
            SELECT COUNT(DISTINCT index_name) FROM information_schema.statistics
            WHERE table_schema=DATABASE() AND table_name='email_outbox'
              AND index_name='idx_outbox_status_created'
            """, ct));
        Assert.Equal(0L, await database.ScalarAsync<long>("""
            SELECT COUNT(*) FROM information_schema.statistics
            WHERE table_schema=DATABASE() AND table_name='upload_sessions' AND index_name='fk_us_project'
            """, ct));
    }

    [Fact(Timeout = 120_000)]
    public async Task AuditKeywordMatchesJsonCaseInsensitivelyAndUsesStableTimeOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await MigratedTestDatabase.CreateOrSkipAsync(ct);
        await database.ExecuteAsync("""
            INSERT INTO audit_logs(action,target_type,target_id,detail,created_at) VALUES
            ('DATA_TEST','test','1',JSON_OBJECT('note','MiXeDMarker'),UTC_TIMESTAMP(3)),
            ('DATA_TEST','test','2',JSON_OBJECT('note','mixedmarker'),UTC_TIMESTAMP(3))
            """, null, ct);
        var http = new DefaultHttpContext();
        http.Request.QueryString = new QueryString("?keyword=mixedmarker&pageSize=20");
        var service = new SystemService(database.Database, new AuditService([]));

        var page = await service.ListLogsAsync(http.Request, ct);

        Assert.Equal(2UL, page.Total);
        Assert.Equal(["2", "1"], page.List.Select(item => item.TargetId).ToArray());
        Assert.Equal(10_001, SystemService.MaximumReportedAuditTotal);

        http.Request.QueryString = new QueryString(
            "?keyword=mixedmarker&start=2026-01-01T00:00:00Z&end=2026-02-02T00:00:00Z");
        var error = await Assert.ThrowsAsync<ApiException>(() => service.ListLogsAsync(http.Request, ct));
        Assert.Equal(400, error.Status);
        Assert.Equal("关键字搜索时间范围不能超过 31 天，请缩小时间范围", error.Message);
    }
}
