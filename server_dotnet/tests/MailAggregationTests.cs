using Dapper;
using MySqlConnector;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Files;

namespace Yf.Api.Tests;

public sealed class MailAggregationTests
{
    [Fact(Timeout = 30_000)]
    public async Task FileSummariesMergeOnlyWithinProjectRecipientAndDirectionAndKeepFirstDeadline()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await MailDeliveryTests.MailDatabaseScope.CreateOrSkipAsync(ct);
        await using var conn = await scope.Database.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM email_outbox", cancellationToken: ct));
        var firstAt = new DateTime(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc);
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);

        await UploadService.EnqueueFileSummaryForRecipientAsync(conn, tx, 7, "项目\r\nInjected", 11,
            "report.pdf\r\nBcc: victim@example.invalid", "C2S", "E-01\r\nX-Test: injected", 20,
            "recipient@example.invalid", "https://portal.example.invalid", firstAt, ct);
        await UploadService.EnqueueFileSummaryForRecipientAsync(conn, tx, 7, "项目", 12,
            "drawing.png", "C2S", "E-02", 20, "recipient@example.invalid",
            "https://portal.example.invalid", firstAt.AddMinutes(1), ct);
        await UploadService.EnqueueFileSummaryForRecipientAsync(conn, tx, 8, "另一个项目", 13,
            "other-project.txt", "C2S", "E-03", 20, "recipient@example.invalid",
            "https://portal.example.invalid", firstAt, ct);
        await UploadService.EnqueueFileSummaryForRecipientAsync(conn, tx, 7, "项目", 14,
            "other-recipient.txt", "C2S", "E-04", 21, "other@example.invalid",
            "https://portal.example.invalid", firstAt, ct);
        await UploadService.EnqueueFileSummaryForRecipientAsync(conn, tx, 7, "项目", 15,
            "reverse.txt", "S2C", "E-05", 20, "recipient@example.invalid",
            "https://portal.example.invalid", firstAt, ct);
        await tx.CommitAsync(ct);

        var rows = (await conn.QueryAsync<SummaryRow>(new CommandDefinition("""
            SELECT project_id AS ProjectId,recipient_user_id AS RecipientUserId,dedupe_key AS DedupeKey,
                   subject AS Subject,body AS Body,next_attempt_at AS NextAttemptAt
            FROM email_outbox ORDER BY id
            """, cancellationToken: ct))).ToArray();
        Assert.Equal(4, rows.Length);
        var merged = Assert.Single(rows, row => row.ProjectId == 7 && row.RecipientUserId == 20
            && row.DedupeKey.EndsWith(":C2S", StringComparison.Ordinal));
        Assert.Equal(firstAt.AddMinutes(2), merged.NextAttemptAt);
        Assert.Contains("target=11", merged.Body, StringComparison.Ordinal);
        Assert.Contains("target=12", merged.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("target=13", merged.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("target=14", merged.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("target=15", merged.Body, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', merged.Subject);
        Assert.DoesNotContain('\n', merged.Subject);
        Assert.DoesNotContain("\nBcc:", merged.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\nX-Test:", merged.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(Timeout = 30_000)]
    public async Task ConcurrentFileSummaryEnqueuesDoNotLoseFilesOrCreateDuplicateWindows()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await MailDeliveryTests.MailDatabaseScope.CreateOrSkipAsync(ct);
        await using (var cleanup = await scope.Database.OpenAsync(ct))
            await cleanup.ExecuteAsync(new CommandDefinition("DELETE FROM email_outbox", cancellationToken: ct));
        var createdAt = new DateTime(2026, 9, 23, 13, 0, 0, DateTimeKind.Utc);
        var concurrentConnectionString = new MySqlConnectionStringBuilder(AppDb.BuildConnectionString(scope.Options))
        {
            MaximumPoolSize = 5,
        }.ConnectionString;

        async Task EnqueueAsync(ulong fileId)
        {
            await using var connection = new MySqlConnection(concurrentConnectionString);
            await connection.OpenAsync(ct);
            await using var transaction = await AppDb.BeginTransactionAsync(connection, ct);
            await UploadService.EnqueueFileSummaryForRecipientAsync(connection, transaction, 7, "并发项目", fileId,
                $"file-{fileId}.txt", "C2S", $"E-{fileId}", 20, "recipient@example.invalid",
                "https://portal.example.invalid", createdAt, ct);
            await transaction.CommitAsync(ct);
        }

        await Task.WhenAll(Enumerable.Range(1, 4).Select(index => EnqueueAsync((ulong)index)));

        await using var check = await scope.Database.OpenAsync(ct);
        var rows = (await check.QueryAsync<SummaryRow>(new CommandDefinition("""
            SELECT project_id AS ProjectId,recipient_user_id AS RecipientUserId,dedupe_key AS DedupeKey,
                   subject AS Subject,body AS Body,next_attempt_at AS NextAttemptAt
            FROM email_outbox
            """, cancellationToken: ct))).ToArray();
        var row = Assert.Single(rows);
        Assert.Equal(createdAt.AddMinutes(2), row.NextAttemptAt);
        for (var fileId = 1; fileId <= 4; fileId++)
            Assert.Equal(1, Count(row.Body, $"target={fileId}"));
    }

    private static int Count(string value, string needle) =>
        value.Split(needle, StringSplitOptions.None).Length - 1;

    private sealed class SummaryRow
    {
        public ulong ProjectId { get; init; }
        public ulong RecipientUserId { get; init; }
        public string DedupeKey { get; init; } = "";
        public string Subject { get; init; } = "";
        public string Body { get; init; } = "";
        public DateTime NextAttemptAt { get; init; }
    }
}
