using Microsoft.EntityFrameworkCore;
using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.Projects;

/// <summary>
/// Unread counts, unread badges and "unread only" filters cover only the last <see cref="Days"/> days;
/// older messages and activities are treated as read. Every unread query is therefore bounded by recent
/// history (served by the created_at / occurred_at indexes) instead of growing with the whole archive.
/// Read receipts themselves (who read which message) are unaffected.
/// </summary>
internal static class UnreadWindow
{
    internal const int Days = 30;

    /// <summary>The oldest timestamp still counted as unread, from the database's UTC clock.</summary>
    internal static async Task<DateTime> CutoffAsync(YfDbContext db, CancellationToken ct) =>
        (await db.Database.SqlQuery<DateTime>($"SELECT UTC_TIMESTAMP(6) AS Value").SingleAsync(ct)).AddDays(-Days);
}
