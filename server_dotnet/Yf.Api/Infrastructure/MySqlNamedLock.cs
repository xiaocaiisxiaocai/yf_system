using MySqlConnector;
using System.Security.Cryptography;
using System.Text;

namespace Yf.Api.Infrastructure;

/// <summary>
/// A server-wide ownership lease backed by MySQL. The lease is bound to the
/// physical connection, so an IIS worker crash or connection loss releases it.
/// A failed explicit release poisons the connection pool generation so that a
/// physical connection which may still own a lock cannot become idle in a pool.
/// </summary>
internal sealed class MySqlNamedLock : IAsyncDisposable
{
    private readonly MySqlConnection connection;
    private readonly string name;
    private int released;

    private MySqlNamedLock(MySqlConnection connection, string name)
    {
        this.connection = connection;
        this.name = name;
    }

    public static string Name(string purpose, string database, params object?[] identity)
    {
        var material = string.Join("\u001f", new[] { database.ToLowerInvariant() }
            .Concat(identity.Select(x => x?.ToString() ?? string.Empty)));
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
        return $"yf:{purpose}:{digest[..40]}";
    }

    public static async Task<MySqlNamedLock?> TryAcquireAsync(
        MySqlConnection connection,
        string name,
        int waitSeconds,
        CancellationToken ct)
    {
        int? acquired;
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT GET_LOCK(@Name,@WaitSeconds)";
            command.Parameters.AddWithValue("@Name", name);
            command.Parameters.AddWithValue("@WaitSeconds", waitSeconds);
            var value = await command.ExecuteScalarAsync(ct);
            acquired = value is null or DBNull ? null : Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch
        {
            // Cancellation can race with the server granting GET_LOCK. Resolve
            // that uncertainty on the same physical connection before returning.
            await ReleaseOrPoisonAsync(connection, name);
            throw;
        }
        if (acquired is null) await ReleaseOrPoisonAsync(connection, name);
        return acquired == 1 ? new MySqlNamedLock(connection, name) : null;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref released, 1) != 0) return;
        await ReleaseOrPoisonAsync(connection, name);
    }

    private static async Task ReleaseOrPoisonAsync(MySqlConnection connection, string name)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT RELEASE_LOCK(@Name)";
            command.Parameters.AddWithValue("@Name", name);
            await command.ExecuteScalarAsync(timeout.Token);
        }
        catch
        {
            // Dispose on a pooled logical connection does not itself prove that
            // the physical MySQL session closed. Clearing its pool generation
            // makes this active connection close physically when returned.
            try { MySqlConnection.ClearPool(connection); }
            catch { }
        }
    }
}
