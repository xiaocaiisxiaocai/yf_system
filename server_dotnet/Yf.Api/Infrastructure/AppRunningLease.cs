using MySqlConnector;

namespace Yf.Api.Infrastructure;

/// <summary>
/// While the web application runs it holds a MySQL named lock on a dedicated, non-pooled
/// connection. Offline maintenance that must never run against a live site (development
/// data reset) probes the same lock with a zero wait and refuses when it is held.
/// The lease is bound to the physical connection, so a crashed worker releases it.
/// Acquisition never blocks startup: during an overlapped IIS recycle the new worker keeps
/// retrying until the old one has released the lease.
/// </summary>
public sealed partial class AppRunningLease(AppOptions options, ILogger<AppRunningLease> logger) : BackgroundService
{
    internal static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(60);

    internal static string LockName(string database) => MySqlNamedLock.Name("app-running", database);

    /// <summary>Tries to take the app-running lock on <paramref name="connection"/> without waiting.</summary>
    internal static Task<MySqlNamedLock?> TryAcquireAsync(MySqlConnection connection, CancellationToken ct) =>
        MySqlNamedLock.TryAcquireAsync(connection, LockName(connection.Database), 0, ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var connectionString = new MySqlConnectionStringBuilder(AppDb.BuildConnectionString(options)) { Pooling = false }.ConnectionString;
        var warned = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = new MySqlConnection(connectionString);
                await connection.OpenAsync(stoppingToken);
                await using var lease = await TryAcquireAsync(connection, stoppingToken);
                if (lease is null)
                {
                    if (!warned)
                        LogLeaseHeldElsewhere(logger);
                    warned = true;
                }
                else
                {
                    if (warned) LogLeaseAcquired(logger);
                    warned = false;
                    while (true)
                    {
                        await Task.Delay(KeepAliveInterval, stoppingToken);
                        await using var ping = connection.CreateCommand();
                        ping.CommandText = "SELECT 1";
                        await ping.ExecuteScalarAsync(stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) when (ex is MySqlException or InvalidOperationException or System.IO.IOException)
            {
                LogLeaseConnectionFailed(logger, ex);
            }
            try { await Task.Delay(RetryInterval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Another process holds the application lease for this database; retrying. Only one Yf.Api process may serve a database.")]
    private static partial void LogLeaseHeldElsewhere(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Application lease acquired")]
    private static partial void LogLeaseAcquired(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Application lease connection failed; retrying")]
    private static partial void LogLeaseConnectionFailed(ILogger logger, Exception exception);
}
