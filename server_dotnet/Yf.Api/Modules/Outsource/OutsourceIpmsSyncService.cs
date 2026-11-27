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

    /// <summary>Starts or stops the daily update; a run already under way finishes, and manual syncs work either way.</summary>
    public async Task<OutsourceSyncStatusResponse> SetScheduleAsync(CurrentUser actor, ScheduleSwitchRequest request, CancellationToken ct)
    {
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var connection = context.Database.Connection();
        var tx = context.Database.RequireTransaction();
        actor = await AccessService.LockActorAsync(connection, tx, actor, ct);
        AccessService.RequireInternal(actor);
        await AccessService.RequirePermissionAsync(connection, tx, actor, OutsourcePermissions.Settings, ct);
        await EnsureRowAsync(context, ct);
        var before = await context.OutsourceSyncState.AsNoTracking().Where(x => x.Id == 1).Select(x => x.ScheduleEnabled).SingleAsync(ct);
        if (before != request.Enabled)
        {
            await context.OutsourceSyncState.Where(x => x.Id == 1).ExecuteUpdateAsync(s => s.SetProperty(x => x.ScheduleEnabled, request.Enabled), ct);
            await audit.WriteAsync(connection, tx, actor.Id, "OUTSOURCE_SYNC_SCHEDULE", "outsource_settings", null, new
            {
                targetName = "更新外协清单定时任务",
                changes = AuditChange.OnlyChanged(new AuditChange("scheduleEnabled", "定时任务", before ? "开启" : "停止", request.Enabled ? "开启" : "停止")),
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
            var vendorKeys = (await OutsourceFieldLayout.LoadAsync(context, ct)).NoticeKeys;
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
                var snapshots = await EffectiveSnapshotsAsync(context, batch, byNo, now, ct);
                var supervisorNotices = await SupervisorNoticesAsync(context, batch, snapshots, vendorKeys, ct);
                foreach (var item in batch)
                {
                    processed++;
                    if (!byNo.TryGetValue(item.WorkOrderNo, out var order)) { missing++; continue; }
                    var result = await ApplyAsync(context, item, order, snapshots[item.Id], now,
                        trigger == "SCHEDULED" ? OutsourceHistory.IpmsScheduled : OutsourceHistory.IpmsManual, actorId, vendorKeys, supervisorNotices.Contains(item.Id), ct);
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

    // A previously imported Excel fallback fills only gaps in this snapshot. Existing records
    // without these values keep ordinary IPMS replacement semantics, including cleared fields.
    private static async Task<Dictionary<ulong, OutsourceItem>> EffectiveSnapshotsAsync(YfDbContext db, OutsourceItem[] batch,
        IReadOnlyDictionary<string, IpmsWorkOrder> orders, DateTime now, CancellationToken ct)
    {
        var snapshots = new Dictionary<ulong, OutsourceItem>();
        foreach (var item in batch)
        {
            if (!orders.TryGetValue(item.WorkOrderNo, out var order)) continue;
            var next = new OutsourceItem();
            OutsourceItemService.ApplyIpms(next, order, now);
            snapshots.Add(item.Id, next);
        }
        if (snapshots.Count == 0) return snapshots;
        var ids = snapshots.Keys.ToArray();
        var fallbacks = await (from value in db.OutsourceItemValues
                               join field in db.OutsourceFields on value.FieldId equals field.Id
                               where ids.Contains(value.ItemId) && field.BuiltIn
                               select new { value.ItemId, field.FieldKey, field.Label, value.Value }).ToArrayAsync(ct);
        foreach (var fallback in fallbacks)
            OutsourceItemService.FillMissingIpmsValue(snapshots[fallback.ItemId], fallback.FieldKey, fallback.Value, fallback.Label);
        return snapshots;
    }

    // A vendor-visible supervisor can change when the directory changes, even if IPMS returns the same owner.
    private static async Task<HashSet<ulong>> SupervisorNoticesAsync(YfDbContext context, OutsourceItem[] batch,
        IReadOnlyDictionary<ulong, OutsourceItem> snapshots, IReadOnlySet<string> vendorKeys, CancellationToken ct)
    {
        const string supervisorKey = "supervisor";
        if (!vendorKeys.Contains(supervisorKey)) return [];
        var sentItems = new List<(OutsourceItem Item, string? NextOwner)>();
        foreach (var item in batch)
        {
            if (item.SendStatus != OutsourceSendStatuses.Sent || !snapshots.TryGetValue(item.Id, out var next)) continue;
            sentItems.Add((item, next.MechanicalOwner));
        }
        var owners = sentItems.Select(x => x.Item.MechanicalOwner).Concat(sentItems.Select(x => x.NextOwner));
        var supervisors = await MechanicalSupervisorDirectory.ResolveAsync(context, owners, ct);
        string? SupervisorOf(string? owner) => owner is null ? null : supervisors.GetValueOrDefault(owner);

        var notices = new HashSet<ulong>();
        foreach (var (item, nextOwner) in sentItems)
        {
            var snapshot = OutsourceChangeMarks.LastNoticeValues(item);
            var before = snapshot is null ? SupervisorOf(item.MechanicalOwner) : snapshot.GetValueOrDefault(supervisorKey);
            if (snapshot is not null && before == OutsourceWorkbook.LegacyBaseline)
            {
                // A snapshot from before 上级主管 existed: adopt what the vendor was last told, i.e. the supervisor of the stored owner.
                before = SupervisorOf(item.MechanicalOwner);
                snapshot[supervisorKey] = before;
                var rebased = System.Text.Json.JsonSerializer.Serialize(snapshot);
                await context.OutsourceItems.Where(x => x.Id == item.Id && x.LastSentSnapshot == item.LastSentSnapshot)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastSentSnapshot, rebased).SetProperty(x => x.UpdatedAt, x => x.UpdatedAt), ct);
            }
            if (before != SupervisorOf(nextOwner)) notices.Add(item.Id);
        }
        return notices;
    }

    private sealed record ItemResult(bool Changed, bool Closed);

    private static async Task<ItemResult> ApplyAsync(YfDbContext context, OutsourceItem item, IpmsWorkOrder order, OutsourceItem next, DateTime now,
        string source, ulong? actorId, IReadOnlySet<string> vendorKeys, bool supervisorNotice, CancellationToken ct)
    {
        // A work order imported before IPMS data exists gets its first due date here.
        var originalDue = item.OriginalDueDate ?? order.DueDate;
        var latestDue = item.LatestDueDate ?? originalDue;
        var changes = AuditChange.OnlyChanged(
            new AuditChange("customer", "", item.Customer, next.Customer),
            new AuditChange("machineModel", "", item.MachineModel, next.MachineModel),
            new AuditChange("mechanicalOwner", "", item.MechanicalOwner, next.MechanicalOwner),
            new AuditChange("electricalOwner", "", item.ElectricalOwner, next.ElectricalOwner),
            new AuditChange("workOrderIssuedOn", "", item.WorkOrderIssuedOn, next.WorkOrderIssuedOn),
            new AuditChange("quantity", "", item.Quantity, next.Quantity),
            new AuditChange("originalDueDate", "", item.OriginalDueDate, originalDue),
            new AuditChange("latestDueDate", "", item.LatestDueDate, latestDue));
        var changed = supervisorNotice || changes.Length > 0;
        DateTime? closedAt = order.Closed ? now : null;

        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await AccessService.LockBusinessAsync(context.Database.Connection(), context.Database.RequireTransaction(), ct);
        var row = context.OutsourceItems.Where(x => x.Id == item.Id && x.UpdatedAt == item.UpdatedAt && x.ClosedAt == null);
        int updated;
        if (changed)
        {
            // A sent item with new IPMS data the vendor sees waits for purchasing to notify the vendor; nothing is mailed here.
            var noticeNeeded = supervisorNotice || changes.Any(x => vendorKeys.Contains(x.Field));
            var sendStatus = noticeNeeded && item.SendStatus == OutsourceSendStatuses.Sent ? OutsourceSendStatuses.PendingNotice : item.SendStatus;
            updated = await row.ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Customer, next.Customer).SetProperty(x => x.MachineModel, next.MachineModel)
                .SetProperty(x => x.MechanicalOwner, next.MechanicalOwner)
                .SetProperty(x => x.ElectricalOwner, next.ElectricalOwner).SetProperty(x => x.WorkOrderIssuedOn, next.WorkOrderIssuedOn)
                .SetProperty(x => x.Quantity, next.Quantity)
                .SetProperty(x => x.LatestDueDate, latestDue).SetProperty(x => x.OriginalDueDate, originalDue)
                .SetProperty(x => x.SendStatus, sendStatus).SetProperty(x => x.IpmsSyncedAt, now)
                .SetProperty(x => x.ClosedAt, closedAt), ct);
        }
        else
        {
            // Nothing changed: record the sync time without bumping the edit version (updated_at stays as is).
            updated = await row.ExecuteUpdateAsync(s => s
                .SetProperty(x => x.IpmsSyncedAt, now).SetProperty(x => x.ClosedAt, closedAt)
                .SetProperty(x => x.UpdatedAt, x => x.UpdatedAt), ct);
        }
        if (updated > 0 && changes.Length > 0)
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
                if (!state.ScheduleEnabled || !IsDue(state.SyncTime, state.ScheduledFor, now, out var day)) continue;
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
        return new(state.SyncTime, state.ScheduleEnabled, await ipms.IsConfiguredAsync(ct), state.LeaseUntil is { } lease && lease > now,
            Utc(state.LastStartedAt), Utc(state.LastFinishedAt), Utc(state.LastSuccessAt), state.LastError, state.LastTrigger,
            state.LastProcessed, state.LastChanged, state.LastClosed, state.LastMissing);
    }

    private static DateTime? Utc(DateTime? value) => value is { } v ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : null;

    private static Task AuthorizeAnyAsync(YfDbContext context, CurrentUser actor, CancellationToken ct) =>
        OutsourceItemService.AuthorizeReadAsync(context, actor, null, ct);
}
