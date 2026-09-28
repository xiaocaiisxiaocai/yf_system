using System.Text.Json;
using Dapper;

namespace Yf.Api.Tests;

[Collection(ConnectionLifecycleCollectionDefinition.Name)]
public sealed class DataQueryPlanEvidenceTests
{
    [Fact(Timeout = 180_000)]
    public async Task CaptureCurrentAndCandidateIndexPlans()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await MigratedTestDatabase.CreateOrSkipAsync(ct);
        await using var connection = await database.Database.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("""
            CREATE TEMPORARY TABLE perf_numbers(n INT NOT NULL PRIMARY KEY);
            INSERT INTO perf_numbers(n)
            SELECT ones.n + tens.n * 10 + hundreds.n * 100 + thousands.n * 1000
            FROM
              (SELECT 0 n UNION ALL SELECT 1 UNION ALL SELECT 2 UNION ALL SELECT 3 UNION ALL SELECT 4 UNION ALL SELECT 5 UNION ALL SELECT 6 UNION ALL SELECT 7 UNION ALL SELECT 8 UNION ALL SELECT 9) ones
            CROSS JOIN
              (SELECT 0 n UNION ALL SELECT 1 UNION ALL SELECT 2 UNION ALL SELECT 3 UNION ALL SELECT 4 UNION ALL SELECT 5 UNION ALL SELECT 6 UNION ALL SELECT 7 UNION ALL SELECT 8 UNION ALL SELECT 9) tens
            CROSS JOIN
              (SELECT 0 n UNION ALL SELECT 1 UNION ALL SELECT 2 UNION ALL SELECT 3 UNION ALL SELECT 4 UNION ALL SELECT 5 UNION ALL SELECT 6 UNION ALL SELECT 7 UNION ALL SELECT 8 UNION ALL SELECT 9) hundreds
            CROSS JOIN
              (SELECT 0 n UNION ALL SELECT 1 UNION ALL SELECT 2 UNION ALL SELECT 3 UNION ALL SELECT 4 UNION ALL SELECT 5 UNION ALL SELECT 6 UNION ALL SELECT 7 UNION ALL SELECT 8 UNION ALL SELECT 9) thousands;

            INSERT INTO users(id,employee_no,password_hash,real_name,email,user_type,status,must_change_password,failed_login_attempts)
            VALUES(2,'plan-user','unused','Plan User','','INTERNAL','ACTIVE',0,0);
            INSERT INTO suppliers(id,name,status,created_by) VALUES(9900,'plan supplier','ACTIVE',1);
            INSERT INTO project_groups(id,name,supplier_id,status,created_by,responsible_user_id)
            VALUES(9900,'plan group',9900,'IN_PROGRESS',1,1);
            INSERT INTO projects(id,project_group_id,name,supplier_id,status,confirm_side,created_by,responsible_user_id,created_at,updated_at)
            SELECT 100000+n,9900,CONCAT('plan-project-',n),9900,
                   IF(MOD(n,10)=0,'PENDING_CONFIRMATION','IN_PROGRESS'),
                   IF(MOD(n,10)=0,'COMPANY',NULL),1,1,
                   DATE_SUB(UTC_TIMESTAMP(3),INTERVAL MOD(n,60) DAY),
                   DATE_SUB(UTC_TIMESTAMP(3),INTERVAL MOD(n,60) DAY)
            FROM perf_numbers;

            INSERT INTO messages(project_id,sender_id,content,status,created_at)
            SELECT 100000,2,CONCAT('message-',n),IF(MOD(n,20)=0,'DELETED','NORMAL'),
                   DATE_SUB(UTC_TIMESTAMP(3),INTERVAL MOD(n,60) DAY)
            FROM perf_numbers;
            INSERT INTO message_reads(message_id,user_id,read_at)
            SELECT id,1,UTC_TIMESTAMP(3) FROM messages WHERE MOD(id,7)=0;

            INSERT INTO email_outbox(event_type,recipient_email,subject,body,status,retry_count,next_attempt_at,last_error,sent_at,created_at)
            SELECT 'MESSAGE_CREATED','plan@example.invalid','subject','body',
                   CASE WHEN n>=9990 AND MOD(n,2)=0 THEN 'PENDING'
                        WHEN n>=9990 THEN 'SENDING' ELSE 'SENT' END,
                   0,
                   CASE WHEN n>=9990 THEN DATE_SUB(UTC_TIMESTAMP(3),INTERVAL 1 MINUTE) ELSE NULL END,
                   NULL,
                   CASE WHEN n<9990 THEN UTC_TIMESTAMP(3) ELSE NULL END,
                   DATE_SUB(UTC_TIMESTAMP(3),INTERVAL MOD(n,120) DAY)
            FROM perf_numbers;
            """, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
            "ANALYZE TABLE projects,project_groups,messages,message_reads,email_outbox",
            cancellationToken: ct));

        var plans = new Dictionary<string, JsonElement>();
        plans["messageUnreadCurrent"] = await ExplainAsync(connection, """
            SELECT COUNT(*) FROM messages m
            WHERE m.project_id=100000 AND m.status='NORMAL' AND m.sender_id<>1
              AND m.created_at>=DATE_SUB(UTC_TIMESTAMP(3),INTERVAL 30 DAY)
              AND NOT EXISTS(SELECT 1 FROM message_reads mr WHERE mr.message_id=m.id AND mr.user_id=1)
            """, ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "CREATE INDEX idx_msg_project_time_cov_candidate ON messages(project_id,created_at,status,sender_id)",
            cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition("ANALYZE TABLE messages", cancellationToken: ct));
        plans["messageUnreadCandidate"] = await ExplainAsync(connection, """
            SELECT COUNT(*) FROM messages m
            WHERE m.project_id=100000 AND m.status='NORMAL' AND m.sender_id<>1
              AND m.created_at>=DATE_SUB(UTC_TIMESTAMP(3),INTERVAL 30 DAY)
              AND NOT EXISTS(SELECT 1 FROM message_reads mr WHERE mr.message_id=m.id AND mr.user_id=1)
            """, ct);

        plans["mailWorkerCurrentOr"] = await ExplainAsync(connection, """
            SELECT eo.id,eo.event_type,eo.recipient_email,eo.subject,eo.body,eo.status,eo.retry_count,eo.next_attempt_at
            FROM email_outbox eo LEFT JOIN users u ON eo.recipient_user_id=u.id
            WHERE eo.event_type<>'STORAGE_WARNING' AND eo.sent_at IS NULL
              AND ((eo.status='PENDING' AND (eo.next_attempt_at IS NULL OR eo.next_attempt_at<=UTC_TIMESTAMP(3)))
                   OR (eo.status='SENDING' AND eo.next_attempt_at<=UTC_TIMESTAMP(3)))
            ORDER BY eo.id LIMIT 10
            """, ct);
        plans["mailWorkerPendingSplitExistingIndex"] = await ExplainAsync(connection, """
            SELECT eo.id FROM email_outbox eo
            WHERE eo.event_type<>'STORAGE_WARNING' AND eo.sent_at IS NULL AND eo.status='PENDING'
              AND (eo.next_attempt_at IS NULL OR eo.next_attempt_at<=UTC_TIMESTAMP(3))
            ORDER BY eo.id LIMIT 10
            """, ct);
        plans["mailWorkerSendingSplitExistingIndex"] = await ExplainAsync(connection, """
            SELECT eo.id FROM email_outbox eo
            WHERE eo.event_type<>'STORAGE_WARNING' AND eo.sent_at IS NULL AND eo.status='SENDING'
              AND eo.next_attempt_at<=UTC_TIMESTAMP(3)
            ORDER BY eo.id LIMIT 10
            """, ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "CREATE INDEX idx_outbox_status_next_id_candidate ON email_outbox(status,next_attempt_at,id)",
            cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition("ANALYZE TABLE email_outbox", cancellationToken: ct));
        plans["mailWorkerCurrentOrWithCandidate"] = await ExplainAsync(connection, """
            SELECT eo.id,eo.event_type,eo.recipient_email,eo.subject,eo.body,eo.status,eo.retry_count,eo.next_attempt_at
            FROM email_outbox eo LEFT JOIN users u ON eo.recipient_user_id=u.id
            WHERE eo.event_type<>'STORAGE_WARNING' AND eo.sent_at IS NULL
              AND ((eo.status='PENDING' AND (eo.next_attempt_at IS NULL OR eo.next_attempt_at<=UTC_TIMESTAMP(3)))
                   OR (eo.status='SENDING' AND eo.next_attempt_at<=UTC_TIMESTAMP(3)))
            ORDER BY eo.id LIMIT 10
            """, ct);

        plans["pendingProjectsCurrent"] = await ExplainAsync(connection, """
            SELECT p.id,p.name,p.project_group_id,g.name,p.confirm_side,p.updated_at
            FROM projects p JOIN project_groups g ON p.project_group_id=g.id
            WHERE p.status='PENDING_CONFIRMATION' AND p.confirm_side='COMPANY'
            ORDER BY p.updated_at DESC,p.id DESC LIMIT 20
            """, ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "CREATE INDEX idx_projects_pending_candidate ON projects(status,confirm_side,updated_at)",
            cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition("ANALYZE TABLE projects", cancellationToken: ct));
        plans["pendingProjectsCandidate"] = await ExplainAsync(connection, """
            SELECT p.id,p.name,p.project_group_id,g.name,p.confirm_side,p.updated_at
            FROM projects p JOIN project_groups g ON p.project_group_id=g.id
            WHERE p.status='PENDING_CONFIRMATION' AND p.confirm_side='COMPANY'
            ORDER BY p.updated_at DESC,p.id DESC LIMIT 20
            """, ct);

        var repository = FindRepositoryRoot();
        var output = Path.Combine(repository, ".artifacts", "reports", "data-system-query-plans.json");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var evidence = new
        {
            capturedAtUtc = DateTime.UtcNow,
            serverVersion = await connection.ExecuteScalarAsync<string>(new CommandDefinition("SELECT VERSION()", cancellationToken: ct)),
            fixtureRows = new { projects = 10_000, messages = 10_000, emailOutbox = 10_000 },
            note = "Temporary migrated database only; candidate indexes are dropped with the database.",
            plans,
        };
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(evidence, TestJson.Indented), ct);
        Assert.True(File.Exists(output));
    }

    private static async Task<JsonElement> ExplainAsync(MySqlConnector.MySqlConnection connection, string sql, CancellationToken ct)
    {
        var json = await connection.ExecuteScalarAsync<string>(new CommandDefinition(
            "EXPLAIN FORMAT=JSON " + sql, cancellationToken: ct))
            ?? throw new InvalidOperationException("EXPLAIN FORMAT=JSON returned no plan document.");
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static string FindRepositoryRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
            if (Directory.Exists(Path.Combine(current.FullName, ".git"))) return current.FullName;
        throw new InvalidOperationException("Repository root not found.");
    }
}
