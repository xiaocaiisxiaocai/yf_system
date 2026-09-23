using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.SystemManagement;

namespace Yf.Api.Tests;

[Collection(ConnectionLifecycleCollection.Name)]
public sealed class MailRetentionTests
{
    [Fact]
    public async Task PurgeRemovesOnlyFinishedRowsOlderThanTheRetentionWindow()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await MigratedTestDatabase.CreateOrSkipAsync(ct);
        database.Options.MailRetentionDays = 90;
        await using (var conn = await database.Database.OpenAsync(ct))
        {
            await conn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO email_outbox(id,event_type,recipient_email,subject,body,status,retry_count,created_at) VALUES
                    (1,'TEST_NOTIFICATION','a@example.invalid','old sent','body','SENT',0,UTC_TIMESTAMP() - INTERVAL 91 DAY),
                    (2,'TEST_NOTIFICATION','a@example.invalid','old failed','body','FAILED',5,UTC_TIMESTAMP() - INTERVAL 91 DAY),
                    (3,'TEST_NOTIFICATION','a@example.invalid','old cancelled','body','CANCELLED',0,UTC_TIMESTAMP() - INTERVAL 91 DAY),
                    (4,'TEST_NOTIFICATION','a@example.invalid','old pending','body','PENDING',0,UTC_TIMESTAMP() - INTERVAL 91 DAY),
                    (5,'TEST_NOTIFICATION','a@example.invalid','recent sent','body','SENT',0,UTC_TIMESTAMP() - INTERVAL 89 DAY)
                """, cancellationToken: ct));
        }
        var service = new MailService(database.Database, database.Options,
            new AuditService(Array.Empty<IProjectAuditCapture>()), NullLogger<MailService>.Instance);

        Assert.Equal(3, await service.PurgeFinishedAsync(ct));

        await using var check = await database.Database.OpenAsync(ct);
        Assert.Equal(new ulong[] { 4, 5 }, (await check.QueryAsync<ulong>(new CommandDefinition(
            "SELECT id FROM email_outbox ORDER BY id", cancellationToken: ct))).ToArray());
        Assert.Equal(0, await service.PurgeFinishedAsync(ct));
    }
}
