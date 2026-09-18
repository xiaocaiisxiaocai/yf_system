using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using MySqlConnector;
using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.Oem.Common;

/// <summary>
/// Unit of work for one OEM business operation: one EF context, at most one
/// READ COMMITTED transaction, the shared management gate, and the database clock
/// captured once. Every OEM write goes through <see cref="BeginAsync"/> so that
/// permission revocation and management changes serialise with business writes
/// exactly as they do in the rest of the system.
/// </summary>
internal sealed class OemUnitOfWork : IAsyncDisposable
{
    private readonly IDbContextTransaction? transaction;

    private OemUnitOfWork(YfDbContext db, IDbContextTransaction? transaction, DateTime now)
    {
        Db = db;
        this.transaction = transaction;
        Now = now;
    }

    public YfDbContext Db { get; }

    /// <summary>Database UTC time read when the unit of work started.</summary>
    public DateTime Now { get; private set; }

    public MySqlConnection Connection => Db.Database.Connection();
    public MySqlTransaction? Transaction => Db.Database.Transaction();
    public bool IsWrite => transaction is not null;

    public static async Task<OemUnitOfWork> BeginAsync(IDbContextFactory<YfDbContext> factory, CancellationToken ct)
    {
        var db = await factory.CreateDbContextAsync(ct);
        try
        {
            var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
            await AccessService.LockBusinessAsync(db.Database.Connection(), db.Database.RequireTransaction(), ct);
            return new OemUnitOfWork(db, tx, await OemClock.NowAsync(db, ct));
        }
        catch
        {
            await db.DisposeAsync();
            throw;
        }
    }

    public static async Task<OemUnitOfWork> ReadAsync(IDbContextFactory<YfDbContext> factory, CancellationToken ct)
    {
        var db = await factory.CreateDbContextAsync(ct);
        try
        {
            await db.Database.OpenConnectionAsync(ct);
            return new OemUnitOfWork(db, null, await OemClock.NowAsync(db, ct));
        }
        catch
        {
            await db.DisposeAsync();
            throw;
        }
    }

    public async Task<DateTime> RefreshNowAsync(CancellationToken ct) => Now = await OemClock.NowAsync(Db, ct);

    public async Task CommitAsync(CancellationToken ct)
    {
        if (transaction is null) throw new InvalidOperationException("Read-only OEM unit of work cannot commit.");
        await transaction.CommitAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (transaction is not null) await transaction.DisposeAsync();
        await Db.DisposeAsync();
    }
}

internal static class OemClock
{
    /// <summary>Database UTC clock with millisecond precision (all OEM timestamps are DATETIME(3)).</summary>
    public static async Task<DateTime> NowAsync(YfDbContext db, CancellationToken ct)
    {
        var now = await db.Database.SqlQuery<DateTime>($"SELECT UTC_TIMESTAMP(3) AS Value").SingleAsync(ct);
        return DateTime.SpecifyKind(now, DateTimeKind.Utc);
    }
}
