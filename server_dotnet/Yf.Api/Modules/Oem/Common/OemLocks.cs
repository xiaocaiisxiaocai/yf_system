using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.Oem.Common;

/// <summary>
/// Primary-key row locks for OEM entities. The table and key column come from the EF model
/// (never from caller text) and are validated before they reach SQL; the key value is always a
/// parameter. Raw SQL stays limited to the lock itself, as the repository requires. Queries that
/// lock by another column, lock several rows or need ORDER BY are written at their call site.
/// <para>
/// <b>Global OEM row-lock order</b> (the single reference; other comments point here). Every
/// write transaction first takes the platform gate (<see cref="OemUnitOfWork"/>: shared business
/// or exclusive management lock), then locks OEM rows only in this order, skipping the ones it
/// does not need:
/// <list type="number">
/// <item><c>oem_transfers</c> — always the first OEM row of any transfer-scoped write.</item>
/// <item>Rows owned by that transfer, each chain in its own order:
///   flow instance → flow task (approval);
///   transfer file → validation job / promotion (validation, promotion, purge, reconcile);
///   transfer file → download session → download lease (delivery, purge);
///   upload session (merge, expiry).
///   Job, promotion and lease rows are resolved without a lock first, locked last and re-validated.</item>
/// <item><c>oem_companies</c> (upload quota, directory management) → <c>oem_accounts</c>.</item>
/// <item><c>oem_accounts</c> is the last OEM row a business writer locks (shared, see
///   <see cref="OemAccountLock"/>); login and session revocation lock account → refresh tokens,
///   several accounts in ascending id order.</item>
/// </list>
/// Management-only rows (flow/retention templates, settings) are written under the exclusive
/// management gate and never interleave with the chains above. Taking rows in any other order can deadlock.
/// </para>
/// </summary>
internal static partial class OemLocks
{
    /// <summary><c>SELECT * … WHERE pk = id FOR UPDATE</c>; compose the terminal operator (Single/SingleOrDefault…) at the call site.</summary>
    public static IQueryable<T> ForUpdate<T>(YfDbContext db, object id) where T : class
    {
        var (table, key) = Target<T>(db);
        return db.Set<T>().FromSqlRaw($"SELECT * FROM `{table}` WHERE `{key}` = {{0}} FOR UPDATE", id);
    }

    /// <summary>Exclusive lock on one row by primary key; null when it does not exist.</summary>
    public static Task<T?> ForUpdateAsync<T>(OemUnitOfWork uow, object id, CancellationToken ct) where T : class =>
        ForUpdate<T>(uow.Db, id).SingleOrDefaultAsync(ct);

    /// <summary>
    /// Shared lock on one row by primary key without loading or tracking it; false when it does not exist.
    /// <c>LOCK IN SHARE MODE</c> keeps MySQL 5.7 compatibility.
    /// </summary>
    public static async Task<bool> ForShareAsync<T>(OemUnitOfWork uow, object id, CancellationToken ct) where T : class
    {
        var (table, key) = Target<T>(uow.Db);
        return await uow.Db.Database
            .SqlQueryRaw<int>($"SELECT 1 AS Value FROM `{table}` WHERE `{key}` = {{0}} LOCK IN SHARE MODE", id)
            .SingleOrDefaultAsync(ct) == 1;
    }

    /// <summary>Resolves and validates the table and single-column primary key of an OEM entity.</summary>
    internal static (string Table, string Key) Target<T>(DbContext db) where T : class
    {
        var entity = db.Model.FindEntityType(typeof(T))
            ?? throw new InvalidOperationException($"{typeof(T).Name} is not mapped in the EF model.");
        var table = entity.GetTableName();
        if (table is null || entity.GetSchema() is not null || !OemTable().IsMatch(table))
            throw new InvalidOperationException($"{typeof(T).Name} is not mapped to an OEM table.");
        var primaryKey = entity.FindPrimaryKey();
        if (primaryKey is not { Properties.Count: 1 })
            throw new InvalidOperationException($"{typeof(T).Name} must have a single-column primary key.");
        var key = primaryKey.Properties[0].GetColumnName(StoreObjectIdentifier.Table(table, null));
        if (key is null || !Identifier().IsMatch(key))
            throw new InvalidOperationException($"{typeof(T).Name} has an invalid primary-key column.");
        return (table, key);
    }

    [GeneratedRegex("^oem_[a-z0-9_]+$", RegexOptions.CultureInvariant)]
    private static partial Regex OemTable();

    [GeneratedRegex("^[a-z][a-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex Identifier();
}
