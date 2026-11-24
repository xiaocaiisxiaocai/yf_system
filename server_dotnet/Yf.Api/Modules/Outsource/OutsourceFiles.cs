using Microsoft.EntityFrameworkCore;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Oem.Data;
using Yf.Api.Modules.Oem.Transfers;
using Yf.Api.Modules.Oem.Validation;

namespace Yf.Api.Modules.Outsource;

public sealed record OutsourceItemFilesResponse(bool CanOpen, OutsourceItemFileResponse[] Files);

public sealed record OutsourceItemFileResponse(
    ulong FileId, ulong TransferId, string Name, ulong SizeBytes, string Direction, string VendorName, DateTime SentAt, string State);

internal static class OutsourceFiles
{
    private const string Processing = "PROCESSING";

    internal static async Task<Dictionary<ulong, (int FileCount, int PurgedFileCount)>> CountsAsync(
        YfDbContext db, IReadOnlyCollection<ulong> itemIds, ulong? companyId, CancellationToken ct)
    {
        if (itemIds.Count == 0) return [];
        var now = await DbClock.UtcNowAsync(db, ct, 3);
        return await Query(db, itemIds, companyId, now).GroupBy(x => x.Transfer.OutsourceItemId!.Value)
            .Select(g => new
            {
                ItemId = g.Key,
                FileCount = g.Sum(x => x.State == PayloadStatuses.Available ? 1 : 0),
                PurgedFileCount = g.Sum(x => x.State == PayloadStatuses.Purged ? 1 : 0),
            })
            .ToDictionaryAsync(x => x.ItemId, x => (x.FileCount, x.PurgedFileCount), ct);
    }

    internal static async Task<OutsourceItemFileResponse[]> ListAsync(
        YfDbContext db, ulong itemId, ulong? companyId, CancellationToken ct)
    {
        var now = await DbClock.UtcNowAsync(db, ct, 3);
        return await (from row in Query(db, [itemId], companyId, now)
         join company in db.OemCompanies.AsNoTracking() on row.Transfer.OemCompanyId equals company.Id
         let sentAt = row.Transfer.SentAt ?? row.Transfer.ReleasedAt ?? row.Transfer.CreatedAt
         orderby sentAt descending, row.File.Id descending
         select new OutsourceItemFileResponse(row.File.Id, row.Transfer.Id, row.File.OriginalName, row.File.SizeBytes,
             row.Transfer.Direction, company.Name, DateTime.SpecifyKind(sentAt, DateTimeKind.Utc), row.State)).ToArrayAsync(ct);
    }

    /// <summary>
    /// A file past its retention deadline can no longer be read (the delivery slice answers 410) even before the purge
    /// worker has changed its status, so it counts as purged here too.
    /// </summary>
    private static IQueryable<ArchiveFile> Query(YfDbContext db, IReadOnlyCollection<ulong> itemIds, ulong? companyId, DateTime now)
    {
        var transfers = db.OemTransfers.AsNoTracking().Where(x => x.LifecycleStatus == TransferLifecycle.Released
            && x.OutsourceItemId.HasValue && itemIds.Contains(x.OutsourceItemId.Value));
        if (companyId.HasValue) transfers = transfers.Where(x => x.OemCompanyId == companyId.Value);

        return from transfer in transfers
               join file in db.OemTransferFiles.AsNoTracking() on transfer.Id equals file.TransferId
               where file.ScanStatus != ValidationStatuses.Invalid && file.ScanStatus != ValidationStatuses.Error
                   && !PayloadStatuses.Missing.Contains(file.PayloadStatus)
               select new ArchiveFile
               {
                   Transfer = transfer,
                   File = file,
                   State = file.PayloadStatus == PayloadStatuses.Purged || file.PayloadStatus == PayloadStatuses.PurgePending
                       || (file.PurgeDueAt != null && file.PurgeDueAt <= now)
                       ? PayloadStatuses.Purged
                       : file.PayloadStatus == PayloadStatuses.Available && file.ScanStatus == ValidationStatuses.Valid
                           ? PayloadStatuses.Available : Processing,
               };
    }

    private sealed class ArchiveFile
    {
        public required OemTransfer Transfer { get; init; }
        public required OemTransferFile File { get; init; }
        public required string State { get; init; }
    }
}
