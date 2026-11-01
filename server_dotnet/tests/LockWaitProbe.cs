using Dapper;
using MySqlConnector;

namespace Yf.Api.Tests;

/// <summary>
/// Condition-based synchronization for tests that hold a lock on one connection and need to know that another
/// statement is genuinely waiting on it, instead of sleeping for a guessed duration.
/// </summary>
internal static class LockWaitProbe
{
    /// <summary>
    /// Completes once any session of the test database is waiting on an InnoDB row lock, or as soon as
    /// <paramref name="stopWhenCompleted"/> finishes (the caller's own assertions then judge the outcome).
    /// Throws <see cref="TimeoutException"/> when neither happens within <paramref name="timeout"/>.
    /// </summary>
    internal static Task WaitForLockWaitAsync(
        string connectionString, CancellationToken ct, Task? stopWhenCompleted = null, TimeSpan? timeout = null) =>
        // MySQL 5.7 does not always list a blocked row-locking statement as LOCK WAIT in innodb_trx, so a locking
        // statement that has been running for a full second (test data is tiny) counts as waiting as well.
        PollAsync(connectionString, """
            SELECT
              (SELECT COUNT(*)
               FROM information_schema.innodb_trx t
               JOIN information_schema.processlist p ON p.ID = t.trx_mysql_thread_id
               WHERE t.trx_state = 'LOCK WAIT' AND p.DB = @databaseName)
              +
              (SELECT COUNT(*)
               FROM information_schema.processlist p
               WHERE p.DB = @databaseName AND p.COMMAND = 'Query' AND p.TIME >= 1
                 AND (p.INFO LIKE '%FOR UPDATE%' OR p.INFO LIKE 'UPDATE %' OR p.INFO LIKE 'DELETE %'))
            """, "an InnoDB lock wait", ct, stopWhenCompleted, timeout);

    /// <summary>
    /// Like <see cref="WaitForLockWaitAsync"/> for a session blocked in <c>GET_LOCK</c> (a MySQL named lock).
    /// </summary>
    internal static Task WaitForNamedLockWaiterAsync(
        string connectionString, CancellationToken ct, Task? stopWhenCompleted = null, TimeSpan? timeout = null) =>
        PollAsync(connectionString, """
            SELECT COUNT(*)
            FROM information_schema.processlist p
            WHERE p.DB = @databaseName AND p.COMMAND = 'Query' AND p.INFO LIKE 'SELECT GET_LOCK(%'
            """, "a named-lock wait", ct, stopWhenCompleted, timeout);

    /// <summary>
    /// Completes once a session of the test database is executing a statement that starts with
    /// <paramref name="statementPrefix"/>. For a statement queued behind a lock that another connection holds
    /// uncommitted, "executing" means blocked on it; use this where InnoDB does not report the wait as a LOCK WAIT.
    /// </summary>
    internal static Task WaitForRunningStatementAsync(
        string connectionString, string statementPrefix, CancellationToken ct, Task? stopWhenCompleted = null,
        TimeSpan? timeout = null) =>
        PollAsync(connectionString, """
            SELECT COUNT(*)
            FROM information_schema.processlist p
            WHERE p.DB = @databaseName AND p.COMMAND = 'Query' AND p.INFO LIKE @pattern
            """, $"a running `{statementPrefix}` statement", ct, stopWhenCompleted, timeout,
            statementPrefix.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%");

    private static async Task PollAsync(
        string connectionString, string countSql, string condition, CancellationToken ct,
        Task? stopWhenCompleted, TimeSpan? timeout, string? pattern = null)
    {
        var builder = new MySqlConnectionStringBuilder(connectionString) { Pooling = false };
        var databaseName = builder.Database;
        await using var monitor = new MySqlConnection(builder.ConnectionString);
        await monitor.OpenAsync(ct);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));
        try
        {
            while (stopWhenCompleted is not { IsCompleted: true })
            {
                var waiting = await monitor.ExecuteScalarAsync<int>(
                    new CommandDefinition(countSql, new { databaseName, pattern }, cancellationToken: deadline.Token));
                if (waiting > 0) return;
                await Task.Delay(20, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            var sessions = await DescribeSessionsAsync(connectionString, databaseName, ct);
            throw new TimeoutException($"No session reached {condition} in `{databaseName}`. Sessions: {sessions}");
        }
    }

    /// <summary>What every session of the test database was doing, so a timed-out wait explains itself.</summary>
    private static async Task<string> DescribeSessionsAsync(string connectionString, string databaseName, CancellationToken ct)
    {
        try
        {
            var builder = new MySqlConnectionStringBuilder(connectionString) { Pooling = false };
            await using var connection = new MySqlConnection(builder.ConnectionString);
            await connection.OpenAsync(ct);
            var rows = await connection.QueryAsync<(long Id, string? Command, string? State, string? Info)>(
                new CommandDefinition(
                    "SELECT ID, COMMAND, STATE, LEFT(INFO, 160) FROM information_schema.processlist WHERE DB = @databaseName",
                    new { databaseName }, cancellationToken: ct));
            return string.Join(" | ", rows.Select(row => $"#{row.Id} {row.Command} {row.State} {row.Info}"));
        }
        catch (Exception error)
        {
            return "unavailable (" + error.GetType().Name + ")";
        }
    }
}
