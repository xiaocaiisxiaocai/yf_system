using System.Data;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Yf.Api.Infrastructure;
using Yf.Api.Infrastructure.Entities;
using Yf.Api.Modules.Outsource.Ipms;

namespace Yf.Api.Modules.Outsource;

/// <summary>
/// Refreshes the IPMS snapshot of open outsourcing items once a day at the configured business time, and on demand.
/// Changes never send mail: an item already sent to its vendor only becomes PENDING_NOTICE for purchasing to review.
/// A failed run keeps the existing data and waits for the next schedule or a manual run. See 外协清单契约 → IPMS 同步.
/// </summary>
public sealed class OutsourceIpmsSyncService(
    IDbContextFactory<YfDbContext> dbFactory,
    AuditService audit,
    IIpmsClient ipms,
    AppOptions options,
    ILogger<OutsourceIpmsSyncService> logger) : BackgroundService
{
    internal const int BatchSize = 200;
    internal const int MaximumManualItems = 500;
    internal const string DefaultSyncTime = "03:30";
    private static readonly TimeSpan ScanInterval = TimeSpan.FromMinutes(1);
    private const int LeaseMinutes = 10;

    internal sealed record Outcome(int Processed, int Changed, int Closed, int Missing, string? Error);

    // ---- API ----

    public async Task<OutsourceSyncStatusResponse> StatusAsync(CurrentUser actor, CancellationToken ct)
    {
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await AuthorizeAnyAsync(context, actor, ct);
        return await StatusAsync(context, ct);
    }

    public async Task<OutsourceSyncStatusResponse> SetSyncTimeAsync(CurrentUser actor, OutsourceSyncTimeRequest request, CancellationToken ct)
    {
        var time = request.SyncTime?.Trim() ?? "";
        if (!TimeOnly.TryParseExact(time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            throw ApiException.BadRequest("同步时间格式需为 HH:mm");
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var connection = context.Database.Connection();
        var tx = context.Database.RequireTransaction();
        actor = await AccessService.LockActorAsync(connection, tx, actor, ct);
        AccessService.RequireInternal(actor);
        await AccessService.RequirePermissionAsync(connection, tx, actor, OutsourcePermissions.Settings, ct);
        await EnsureRowAsync(context, ct);
        var before = await context.OutsourceSyncState.AsNoTracking().Where(x => x.Id == 1).Select(x => x.SyncTime).SingleAsync(ct);
        if (before != time)
        {
            await context.OutsourceSyncState.Where(x => x.Id == 1).ExecuteUpdateAsync(s => s.SetProperty(x => x.SyncTime, time), ct);
            await audit.WriteAsync(connection, tx, actor.Id, "OUTSOURCE_SYNC_TIME", "outsource_settings", null, new
            {
                targetName = "IPMS 同步时间",
                changes = AuditChange.OnlyChanged(new AuditChange("syncTime", "每日同步时间", before, time)),
            }, null, ct);
        }
        await transaction.CommitAsync(ct);
        return await StatusAsync(context, ct);
    }

    /// <summary>Runs a sync now for the selected open items (or all of them) and returns the resulting status.</summary>
    public async Task<OutsourceSyncStatusResponse> SyncNowAsync(CurrentUser actor, OutsourceSyncRequest request, CancellationToken ct)
    {
        var ids = request.ItemIds?.Distinct().ToArray();
        if (ids is { Length: > MaximumManualItems }) throw ApiException.BadRequest($"一次最多同步 {MaximumManualItems} 条");
        await using (var context = await dbFactory.CreateDbContextAsync(ct))
        {
            AccessService.RequireInternal(actor);
            await context.Database.OpenConnectionAsync(ct);
            actor = await AccessService.ReadActorAsync(context.Database.Connection(), null, actor, ct);
            AccessService.RequireInternal(actor);
            await AccessService.RequirePermissionAsync(context.Database.Connection(), null, actor, OutsourcePermissions.Manage, ct);
        }
        var outcome = await RunAsync("MANUAL", actor.Id, ids is { Length: > 0 } ? ids : null, null, ct)
            ?? throw ApiException.Conflict("IPMS 同步正在进行，请稍后再试");
        if (outcome.Error is not null) throw ApiException.Unavailable(outcome.Error);
        await using var reader = await dbFactory.CreateDbContextAsync(ct);
        return await StatusAsync(reader, ct);
    }

    // ---- run ----

    /// <summary>
    /// One synchronization pass. Returns null when another run holds the lease (or, for a scheduled run, today's run
    /// already started). Item updates are compare-and-set on <c>updated_at</c>, so a concurrent purchasing edit wins and
    /// the item is refreshed on the next run.
    /// </summary>
    internal async Task<Outcome?> RunAsync(string trigger, ulong? actorId, ulong[]? itemIds, DateOnly? scheduledFor, CancellationToken ct)
    {
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await context.Database.OpenConnectionAsync(ct);
        await EnsureRowAsync(context, ct);
        var claimed = scheduledFor is { } day
            ? await context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE outsource_sync_state
                SET lease_until=UTC_TIMESTAMP(3) + INTERVAL {LeaseMinutes} MINUTE, last_started_at=UTC_TIMESTAMP(3),
                    last_trigger={trigger}, scheduled_for={day}
                WHERE id=1 AND (lease_until IS NULL OR lease_until < UTC_TIMESTAMP(3))
                  AND (scheduled_for IS NULL OR scheduled_for < {day})
                """, ct)
            : await context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE outsource_sync_state
                SET lease_until=UTC_TIMESTAMP(3) + INTERVAL {LeaseMinutes} MINUTE, last_started_at=UTC_TIMESTAMP(3), last_trigger={trigger}
                WHERE id=1 AND (lease_until IS NULL OR lease_until < UTC_TIMESTAMP(3))
                """, ct);
        if (claimed == 0) return null;

        int processed = 0, changed = 0, closed = 0, missing = 0;
        string? error = null;
        try
        {
            ulong afterId = 0;
            while (true)
            {
                var batch = await context.OutsourceItems.AsNoTracking()
                    .Where(x => x.Id > afterId && x.ClosedAt == null && (itemIds == null || itemIds.Contains(x.Id)))
                    .OrderBy(x => x.Id).Take(BatchSize).ToArrayAsync(ct);
                if (batch.Length == 0) break;
                afterId = batch[^1].Id;
                IReadOnlyDictionary<string, IpmsWorkOrder> found;
                try { found = await ipms.LookupAsync(batch.Select(x => x.WorkOrderNo).ToArray(), ct); }
                catch (IpmsUnavailableException unavailable) { error = unavailable.Message; break; }
                var byNo = found.Values.ToDictionary(x => x.WorkOrderNo, StringComparer.OrdinalIgnoreCase);
                var now = await DbClock.UtcNowAsync(context, ct, 3);
                foreach (var item in batch)
                {
                    processed++;
                    if (!byNo.TryGetValue(item.WorkOrderNo, out var order)) { missing++; continue; }
                    var result = await ApplyAsync(context, item, order, now,
                        trigger == "SCHEDULED" ? OutsourceHistory.IpmsScheduled : OutsourceHistory.IpmsManual, actorId, ct);
                    if (result.Changed) changed++;
                    if (result.Closed) closed++;
                }
                await context.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE outsource_sync_state SET lease_until=UTC_TIMESTAMP(3) + INTERVAL {LeaseMinutes} MINUTE WHERE id=1", ct);
                if (batch.Length < BatchSize) break;
            }
        }
        catch (Exception unexpected) when (unexpected is not OperationCanceledException)
        {
            error = "同步失败，请稍后重试";
            logger.LogWarning(unexpected, "Outsourcing IPMS sync failed.");
        }
        finally
        {
            // Release even when cancelled so the next run is not blocked for the lease period.
            var errorText = error;
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE outsource_sync_state
                SET lease_until=NULL, last_finished_at=UTC_TIMESTAMP(3),
                    last_success_at=CASE WHEN {errorText} IS NULL THEN UTC_TIMESTAMP(3) ELSE last_success_at END,
                    last_error={errorText}, last_processed={processed}, last_changed={changed},
                    last_closed={closed}, last_missing={missing}
                WHERE id=1
                """, CancellationToken.None);
        }
        await audit.WriteAsync(context.Database.Connection(), null, actorId, "OUTSOURCE_IPMS_SYNC", "outsource_item", null, new
        {
            targetName = "IPMS 同步", trigger, processed, changed, closed, missing, error,
            selected = itemIds?.Length,
        }, null, ct);
        return new(processed, changed, closed, missing, error);
    }

    private sealed record ItemResult(bool Changed, bool Closed);

    private static async Task<ItemResult> ApplyAsync(YfDbContext context, OutsourceItem item, IpmsWorkOrder order, DateTime now,
        string source, ulong? actorId, CancellationToken ct)
    {
        var next = new OutsourceItem();
        OutsourceItemService.ApplyIpms(next, order, now);
        var changes = AuditChange.OnlyChanged(
            new AuditChange("customer", "", item.Customer, next.Customer),
            new AuditChange("machineModel", "", item.MachineModel, next.MachineModel),
            new AuditChange("mechanicalSection", "", item.MechanicalSection, next.MechanicalSection),
            new AuditChange("mechanicalOwner", "", item.MechanicalOwner, next.MechanicalOwner),
            new AuditChange("electricalOwner", "", item.ElectricalOwner, next.ElectricalOwner),
            new AuditChange("workOrderIssuedOn", "", item.WorkOrderIssuedOn, next.WorkOrderIssuedOn),
            new AuditChange("bomOn", "", item.BomOn, next.BomOn),
            new AuditChange("drawingReleasedOn", "", item.DrawingReleasedOn, next.DrawingReleasedOn),
            new AuditChange("quantity", "", item.Quantity, next.Quantity),
            new AuditChange("latestDueDate", "", item.LatestDueDate, next.LatestDueDate));
        var changed = changes.Length > 0;
        var originalDue = item.OriginalDueDate ?? next.LatestDueDate;
        DateTime? closedAt = order.Closed ? now : null;

        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await AccessService.LockBusinessAsync(context.Database.Connection(), context.Database.RequireTransaction(), ct);
        var row = context.OutsourceItems.Where(x => x.Id == item.Id && x.UpdatedAt == item.UpdatedAt && x.ClosedAt == null);
        int updated;
        if (changed)
        {
            // A sent item with new IPMS data waits for purchasing to notify the vendor; nothing is mailed here.
            var sendStatus = item.SendStatus == OutsourceSendStatuses.Sent ? OutsourceSendStatuses.PendingNotice : item.SendStatus;
            updated = await row.ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Customer, next.Customer).SetProperty(x => x.MachineModel, next.MachineModel)
                .SetProperty(x => x.MechanicalSection, next.MechanicalSection).SetProperty(x => x.MechanicalOwner, next.MechanicalOwner)
                .SetProperty(x => x.ElectricalOwner, next.ElectricalOwner).SetProperty(x => x.WorkOrderIssuedOn, next.WorkOrderIssuedOn)
                .SetProperty(x => x.BomOn, next.BomOn).SetProperty(x => x.DrawingReleasedOn, next.DrawingReleasedOn)
                .SetProperty(x => x.Quantity, next.Quantity)
                .SetProperty(x => x.LatestDueDate, next.LatestDueDate).SetProperty(x => x.OriginalDueDate, originalDue)
                .SetProperty(x => x.SendStatus, sendStatus).SetProperty(x => x.IpmsSyncedAt, now)
                .SetProperty(x => x.ClosedAt, closedAt), ct);
        }
        else
        {
            // Nothing changed: record the sync time without bumping the edit version (updated_at stays as is).
            updated = await row.ExecuteUpdateAsync(s => s
                .SetProperty(x => x.IpmsSyncedAt, now).SetProperty(x => x.OriginalDueDate, originalDue)
                .SetProperty(x => x.ClosedAt, closedAt).SetProperty(x => x.UpdatedAt, x => x.UpdatedAt), ct);
        }
        if (updated > 0 && changed)
        {
            OutsourceHistory.Add(context, item.Id, changes, source, actorId);
            await context.SaveChangesAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return updated == 0 ? new(false, false) : new(changed, order.Closed);
    }

    // ---- schedule ----

    /// <summary>Whether the scheduled run for the current business day (UTC+8) is due.</summary>
    internal static bool IsDue(string syncTime, DateOnly? scheduledFor, DateTime utcNow, out DateOnly businessDay)
    {
        var local = utcNow.AddHours(8);
        businessDay = DateOnly.FromDateTime(local);
        if (!TimeOnly.TryParseExact(syncTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)) at = new(3, 30);
        return TimeOnly.FromDateTime(local) >= at && (scheduledFor is null || scheduledFor < businessDay);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.WorkerEnabled) return;
        using var timer = new PeriodicTimer(ScanInterval);
        do
        {
            try
            {
                if (!await ipms.IsConfiguredAsync(stoppingToken)) continue;
                await using var context = await dbFactory.CreateDbContextAsync(stoppingToken);
                await EnsureRowAsync(context, stoppingToken);
                var state = await context.OutsourceSyncState.AsNoTracking().SingleAsync(x => x.Id == 1, stoppingToken);
                var now = await DbClock.UtcNowAsync(context, stoppingToken, 3);
                if (!IsDue(state.SyncTime, state.ScheduledFor, now, out var day)) continue;
                var outcome = await RunAsync("SCHEDULED", null, null, day, stoppingToken);
                if (outcome is not null)
                    logger.LogInformation("Outsourcing IPMS sync: {Processed} processed, {Changed} changed, {Closed} closed, {Missing} missing, error {Error}.",
                        outcome.Processed, outcome.Changed, outcome.Closed, outcome.Missing, outcome.Error);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogWarning(error, "Outsourcing IPMS sync scheduler failed; retrying next cycle."); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    // ---- helpers ----

    private static Task EnsureRowAsync(YfDbContext context, CancellationToken ct) =>
        context.Database.ExecuteSqlInterpolatedAsync($"INSERT IGNORE INTO outsource_sync_state(id,sync_time) VALUES(1,{DefaultSyncTime})", ct);

    private async Task<OutsourceSyncStatusResponse> StatusAsync(YfDbContext context, CancellationToken ct)
    {
        await EnsureRowAsync(context, ct);
        var state = await context.OutsourceSyncState.AsNoTracking().SingleAsync(x => x.Id == 1, ct);
        var now = await DbClock.UtcNowAsync(context, ct, 3);
        return new(state.SyncTime, await ipms.IsConfiguredAsync(ct), state.LeaseUntil is { } lease && lease > now,
            Utc(state.LastStartedAt), Utc(state.LastFinishedAt), Utc(state.LastSuccessAt), state.LastError, state.LastTrigger,
            state.LastProcessed, state.LastChanged, state.LastClosed, state.LastMissing);
    }

    private static DateTime? Utc(DateTime? value) => value is { } v ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : null;

    private static async Task AuthorizeAnyAsync(YfDbContext context, CurrentUser actor, CancellationToken ct)
    {
        AccessService.RequireInternal(actor);
        await context.Database.OpenConnectionAsync(ct);
        var connection = context.Database.Connection();
        actor = await AccessService.ReadActorAsync(connection, null, actor, ct);
        AccessService.RequireInternal(actor);
        var codes = await AccessService.PermissionCodesAsync(connection, null, actor.Id, ct);
        if (!codes.Any(code => code is OutsourcePermissions.View or OutsourcePermissions.Settings or OutsourcePermissions.Manage))
            throw ApiException.Forbidden();
    }
}
