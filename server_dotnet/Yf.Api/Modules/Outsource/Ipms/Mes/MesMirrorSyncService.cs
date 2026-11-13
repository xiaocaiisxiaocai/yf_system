using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yf.Api.Infrastructure;
using Yf.Api.Infrastructure.Entities;

namespace Yf.Api.Modules.Outsource.Ipms.Mes;

/// <summary>
/// Copies the whole IPMS MES monitoring list into <c>mes_work_orders</c>, once a day and on demand. Pages are collected in a staging
/// table and swapped in, in one transaction, only after the fetch is complete (distinct work orders equal MES's total) and has
/// not collapsed against the previous success. Any failure keeps yesterday's mirror. A manual run can fetch one page or be
/// stopped; what it fetched then waits in staging until a person discards it or merges it (never deleting rows).
/// See 设计 → 抓取流程、手动抓取.
/// </summary>
public sealed class MesMirrorSyncService(
    IDbContextFactory<YfDbContext> dbFactory,
    AuditService audit,
    IMesSource source,
    DbMesSettings settings,
    AppOptions options,
    ILogger<MesMirrorSyncService> logger) : BackgroundService
{
    private const int LeaseMinutes = 60;
    private const int Retries = 2;
    private const int PreviewRows = 50;
    private const string Generic = "同步失败，请稍后重试";
    private static readonly TimeSpan ScanInterval = TimeSpan.FromMinutes(1);

    private const string Idle = "IDLE", Running = "RUNNING", Stopping = "STOPPING", AwaitingConfirm = "AWAITING_CONFIRM";

    internal sealed record MirrorOutcome(int Fetched, int Pages, string? Error);

    /// <summary>What one fetch has collected so far (all of it already in the staging table).</summary>
    private sealed class Crawl
    {
        public int Total = -1, Received, Duplicates, Pages;
        public readonly HashSet<string> Seen = new(StringComparer.Ordinal);
        public readonly string BatchId = Guid.NewGuid().ToString();
    }

    // ---- scheduled run ----

    /// <summary>
    /// One scheduled or test-driven pass over every page. Returns null when another run holds the lease (or, for a scheduled
    /// run, that business day already started). A failed pass keeps the existing mirror and records the reason.
    /// </summary>
    internal async Task<MirrorOutcome?> RunAsync(string trigger, DateOnly? scheduledFor, CancellationToken ct)
    {
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await context.Database.OpenConnectionAsync(ct);
        if (!await ClaimLeaseAsync(context, trigger, scheduledFor, ct)) return null;
        DiscardStalePendingResult();

        var crawl = new Crawl();
        string? error = null;
        try
        {
            var previous = await context.MesMirrorState.AsNoTracking().Where(x => x.Id == 1).Select(x => x.LastCount).SingleAsync(ct);
            await context.MesWorkOrdersStaging.ExecuteDeleteAsync(ct);
            await CrawlAsync(context, crawl, options.Ipms.MesPageSize, null, null, ct);
            error = Validate(crawl, previous);
            if (error is null) await ReplaceMirrorAsync(context, ct);
        }
        catch (IpmsUnavailableException unavailable)
        {
            error = await SanitizeAsync(unavailable.Message);
        }
        catch (Exception unexpected) when (unexpected is not OperationCanceledException)
        {
            error = Generic;
            logger.LogWarning(unexpected, "MES mirror sync failed.");
        }
        finally
        {
            // Release even when cancelled so the next run is not blocked for the lease period.
            await FinishAsync(context, error, error is null ? crawl.Seen.Count : null);
        }
        await WriteAuditAsync(context, null, new { targetName = "IPMS 资料抓取", trigger, fetched = crawl.Seen.Count, pages = crawl.Pages, error }, ct);
        return new(crawl.Seen.Count, crawl.Pages, error);
    }

    private static async Task<bool> ClaimLeaseAsync(YfDbContext context, string trigger, DateOnly? scheduledFor, CancellationToken ct)
    {
        await context.Database.ExecuteSqlRawAsync("INSERT IGNORE INTO mes_mirror_state(id) VALUES(1)", ct);
        var claimed = scheduledFor is { } day
            ? await context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE mes_mirror_state
                SET lease_until=UTC_TIMESTAMP(3) + INTERVAL {LeaseMinutes} MINUTE, last_started_at=UTC_TIMESTAMP(3),
                    last_trigger={trigger}, scheduled_for={day}
                WHERE id=1 AND (lease_until IS NULL OR lease_until < UTC_TIMESTAMP(3))
                  AND (scheduled_for IS NULL OR scheduled_for < {day})
                """, ct)
            : await context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE mes_mirror_state
                SET lease_until=UTC_TIMESTAMP(3) + INTERVAL {LeaseMinutes} MINUTE, last_started_at=UTC_TIMESTAMP(3), last_trigger={trigger}
                WHERE id=1 AND (lease_until IS NULL OR lease_until < UTC_TIMESTAMP(3))
                """, ct);
        return claimed > 0;
    }

    /// <summary>
    /// Fetches pages into the staging table (all pages, or only <paramref name="onlyPage"/>). The token only interrupts the
    /// fetch or the pause between pages: a page that has been fetched is always written, so a stop never loses it.
    /// </summary>
    private async Task CrawlAsync(YfDbContext context, Crawl crawl, int pageSize, int? onlyPage, Action<Crawl>? report, CancellationToken ct)
    {
        var maxPages = int.MaxValue;
        for (var page = onlyPage ?? 1; ; page++)
        {
            var result = await FetchWithRetryAsync(page, pageSize, ct);
            if (crawl.Pages == 0)
            {
                crawl.Total = result.Total;
                maxPages = Math.Max(1, (int)(((long)crawl.Total + pageSize - 1) / pageSize)) + 5;
            }
            crawl.Pages++;
            crawl.Received += result.Rows.Count;
            var fetchedAt = await DbClock.UtcNowAsync(context, CancellationToken.None, 3);
            foreach (var row in result.Rows)
            {
                if (!crawl.Seen.Add(row.WorkOrderNo)) { crawl.Duplicates++; continue; }
                context.MesWorkOrdersStaging.Add(Stage(row, crawl.BatchId, fetchedAt));
            }
            await context.SaveChangesAsync(CancellationToken.None);
            context.ChangeTracker.Clear();
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE mes_mirror_state SET lease_until=UTC_TIMESTAMP(3) + INTERVAL {LeaseMinutes} MINUTE WHERE id=1", CancellationToken.None);
            report?.Invoke(crawl);
            if (onlyPage is not null || result.Rows.Count == 0 || crawl.Received >= crawl.Total || crawl.Pages >= maxPages) break;
            if (options.Ipms.MesPageDelaySeconds > 0) await Task.Delay(TimeSpan.FromSeconds(options.Ipms.MesPageDelaySeconds), ct);
        }
    }

    /// <summary>Why a complete fetch must not replace the mirror, or null when it may.</summary>
    private string? Validate(Crawl crawl, int previous)
    {
        if (crawl.Seen.Count != crawl.Total)
            return $"抓取到 {crawl.Seen.Count} 笔，与 IPMS 总数 {crawl.Total} 不一致" + (crawl.Duplicates > 0 ? $"（含 {crawl.Duplicates} 笔重复工令）" : "");
        if (previous > 0 && (long)crawl.Seen.Count * 100 < (long)previous * options.Ipms.MesMinRatioPercent)
            return $"笔数骤降：本次 {crawl.Seen.Count} 笔，上次 {previous} 笔，已保留上次数据";
        return null;
    }

    private static async Task ReplaceMirrorAsync(YfDbContext context, CancellationToken ct)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await context.MesWorkOrders.ExecuteDeleteAsync(ct);
        await context.Database.ExecuteSqlRawAsync("""
            INSERT INTO mes_work_orders(work_order_no,batch_id,fetched_at,customer_name,device_models,raw_json)
            SELECT work_order_no,batch_id,fetched_at,customer_name,device_models,raw_json FROM mes_work_orders_staging
            """, ct);
        await transaction.CommitAsync(ct);
    }

    /// <summary>Clears staging, releases the lease and records the outcome; the count is only stored for a successful replacement.</summary>
    private async Task FinishAsync(YfDbContext context, string? error, int? replacedCount)
    {
        try { await context.Database.ExecuteSqlRawAsync("DELETE FROM mes_work_orders_staging", CancellationToken.None); }
        catch (Exception cleanup) { logger.LogWarning(cleanup, "MES mirror staging cleanup failed."); }
        var count = replacedCount ?? 0;
        var success = error is null && replacedCount is not null;
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE mes_mirror_state
            SET lease_until=NULL, last_finished_at=UTC_TIMESTAMP(3),
                last_success_at=CASE WHEN {success} THEN UTC_TIMESTAMP(3) ELSE last_success_at END,
                last_count=CASE WHEN {success} THEN {count} ELSE last_count END,
                last_error={error}
            WHERE id=1
            """, CancellationToken.None);
    }

    private Task WriteAuditAsync(YfDbContext context, ulong? actorId, object detail, CancellationToken ct) =>
        audit.WriteAsync(context.Database.Connection(), null, actorId, "OUTSOURCE_MES_MIRROR", "mes_work_orders", null, detail, null, ct);

    private async Task<MesPage> FetchWithRetryAsync(int page, int pageSize, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return await source.FetchPageAsync(page, pageSize, ct); }
            catch (IpmsUnavailableException) when (attempt < Retries)
            {
                var wait = TimeSpan.FromSeconds(options.Ipms.MesPageDelaySeconds * 5);
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            }
        }
    }

    private static MesWorkOrderStaging Stage(MesRow row, string batchId, DateTime fetchedAt)
    {
        string? customer = null, models = null;
        using var json = JsonDocument.Parse(row.RawJson);
        if (json.RootElement.TryGetProperty("customerName", out var c) && c.ValueKind == JsonValueKind.String) customer = Cut(c.GetString());
        if (json.RootElement.TryGetProperty("deviceModels", out var m) && m.ValueKind == JsonValueKind.String) models = Cut(m.GetString());
        return new() { WorkOrderNo = Cut(row.WorkOrderNo, 64)!, BatchId = batchId, FetchedAt = fetchedAt, CustomerName = customer, DeviceModels = models, RawJson = row.RawJson };
    }

    private static string? Cut(string? value, int length = 256) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim() is { } text && text.Length > length ? text[..length] : value.Trim();

    /// <summary>Last line of defence: no stored or logged text may carry the IPMS account or password (saved or configured).</summary>
    private async Task<string> SanitizeAsync(string text)
    {
        var current = await settings.ConnectionAsync(CancellationToken.None);
        foreach (var secret in new[] { current.Password, current.Account, options.Ipms.MesPassword, options.Ipms.MesAccount })
            if (!string.IsNullOrEmpty(secret)) text = text.Replace(secret, "***", StringComparison.Ordinal);
        return text.Length > 500 ? text[..500] : text;
    }

    // ---- manual runs ----

    /// <summary>The single in-memory manual run (the app is single-instance); its lease and staged rows live in the database.</summary>
    private sealed class ManualRun(string mode, int? page, int pageSize)
    {
        public string Mode { get; } = mode;
        public int? Page { get; } = page;
        public int PageSize { get; } = pageSize;
        public string Phase = Running;
        /// <summary>Set while a confirm is being applied so a second confirm is refused.</summary>
        public bool Confirming;
        public string? PendingReason;
        public int Fetched, Pages, Total = -1;
        public DateTime StartedAt = DateTime.UtcNow;
        public readonly CancellationTokenSource Cts = new();
        public Task Task = Task.CompletedTask;
    }

    private readonly object gate = new();
    private ManualRun? manual;

    public async Task<MesMirrorStatusResponse> StatusAsync(CurrentUser actor, CancellationToken ct)
    {
        await AuthorizeAsync(actor, ct);
        return await BuildStatusAsync(ct);
    }

    public async Task<MesMirrorStatusResponse> StartAsync(CurrentUser actor, MesMirrorRunRequest request, CancellationToken ct)
    {
        var mode = request.Mode?.Trim().ToUpperInvariant();
        if (mode is not ("ALL" or "PAGE")) throw ApiException.BadRequest("抓取方式只能是全部或指定页");
        var pageSize = request.PageSize ?? options.Ipms.MesPageSize;
        if (pageSize is < 1 or > 200) throw ApiException.BadRequest("每页条数需为 1 到 200");
        if (mode == "PAGE" && request.Page is not >= 1) throw ApiException.BadRequest("页码需大于或等于 1");
        var actorId = await AuthorizeAsync(actor, ct);

        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await context.Database.OpenConnectionAsync(ct);
        if (!await ClaimLeaseAsync(context, "MANUAL", null, ct)) throw ApiException.Conflict("已有抓取任务进行中或等待确认");
        var run = new ManualRun(mode, mode == "PAGE" ? request.Page : null, pageSize);
        lock (gate)
        {
            manual = run;
            run.Task = Task.Run(() => RunManualGuardedAsync(run, actorId));
        }
        return await BuildStatusAsync(ct);
    }

    public async Task<MesMirrorStatusResponse> StopAsync(CurrentUser actor, CancellationToken ct)
    {
        await AuthorizeAsync(actor, ct);
        Task? finished = null;
        lock (gate)
        {
            if (manual is { Phase: Running or Stopping } run)
            {
                run.Phase = Stopping;
                run.Cts.Cancel();
                finished = run.Task;
            }
        }
        // A replacement already under way cannot be interrupted; waiting for it keeps the answer truthful.
        if (finished is not null) await Task.WhenAny(finished, Task.Delay(TimeSpan.FromSeconds(30), ct));
        return await BuildStatusAsync(ct);
    }

    public async Task<MesMirrorStatusResponse> ConfirmAsync(CurrentUser actor, MesMirrorConfirmRequest request, CancellationToken ct)
    {
        var actorId = await AuthorizeAsync(actor, ct);
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await context.Database.OpenConnectionAsync(ct);
        var leaseHeld = await LeaseHeldAsync(context, ct);
        ManualRun run;
        lock (gate)
        {
            if (!leaseHeld || manual is not { Phase: AwaitingConfirm, Confirming: false } pending) throw ApiException.Conflict("没有等待确认的抓取结果");
            run = pending;
            run.Confirming = true;
        }
        var merged = 0;
        try
        {
            if (request.Apply)
            {
                await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
                merged = await context.Database.ExecuteSqlRawAsync("""
                    INSERT INTO mes_work_orders(work_order_no,batch_id,fetched_at,customer_name,device_models,raw_json)
                    SELECT work_order_no,batch_id,fetched_at,customer_name,device_models,raw_json FROM mes_work_orders_staging
                    ON DUPLICATE KEY UPDATE batch_id=VALUES(batch_id), fetched_at=VALUES(fetched_at), customer_name=VALUES(customer_name),
                        device_models=VALUES(device_models), raw_json=VALUES(raw_json)
                    """, ct);
                await transaction.CommitAsync(ct);
            }
            await context.Database.ExecuteSqlRawAsync("DELETE FROM mes_work_orders_staging", ct);
            await context.Database.ExecuteSqlRawAsync("""
                UPDATE mes_mirror_state
                SET lease_until=NULL, last_finished_at=UTC_TIMESTAMP(3), last_error=NULL,
                    last_count=(SELECT COUNT(*) FROM mes_work_orders)
                WHERE id=1
                """, ct);
        }
        catch
        {
            lock (gate) { run.Confirming = false; }
            throw;
        }
        lock (gate) { if (ReferenceEquals(manual, run)) manual = null; }
        run.Cts.Dispose();
        await WriteAuditAsync(context, actorId, new
        {
            targetName = "IPMS 资料抓取", trigger = "MANUAL", mode = run.Mode, action = request.Apply ? "MERGE" : "DISCARD", fetched = run.Fetched, merged,
        }, ct);
        return await BuildStatusAsync(ct);
    }

    /// <summary>Nothing may leave a manual run stuck in RUNNING with the lease held: anything unexpected resets it.</summary>
    private async Task RunManualGuardedAsync(ManualRun run, ulong actorId)
    {
        try { await RunManualAsync(run, actorId); }
        catch (Exception error)
        {
            logger.LogWarning(error, "Manual MES mirror run failed unexpectedly.");
            lock (gate) { if (ReferenceEquals(manual, run)) manual = null; }
            try
            {
                await using var context = await dbFactory.CreateDbContextAsync(CancellationToken.None);
                await FinishAsync(context, Generic, null);
            }
            catch (Exception cleanup) { logger.LogWarning(cleanup, "Manual MES mirror run cleanup failed."); }
        }
    }

    private async Task RunManualAsync(ManualRun run, ulong actorId)
    {
        await using var context = await dbFactory.CreateDbContextAsync(CancellationToken.None);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        var crawl = new Crawl();
        string? error = null;
        var pending = false;
        int? replaced = null;
        try
        {
            var previous = await context.MesMirrorState.AsNoTracking().Where(x => x.Id == 1).Select(x => x.LastCount).SingleAsync(CancellationToken.None);
            await context.MesWorkOrdersStaging.ExecuteDeleteAsync(CancellationToken.None);
            void Report(Crawl c) { lock (gate) { run.Fetched = c.Seen.Count; run.Pages = c.Pages; run.Total = c.Total; } }
            await CrawlAsync(context, crawl, run.PageSize, run.Page, Report, run.Cts.Token);
            if (run.Mode == "PAGE") pending = crawl.Seen.Count > 0;
            else
            {
                error = Validate(crawl, previous);
                if (error is null)
                {
                    await ReplaceMirrorAsync(context, CancellationToken.None); // never interrupted once it starts
                    replaced = crawl.Seen.Count;
                }
            }
        }
        catch (OperationCanceledException) when (run.Cts.IsCancellationRequested)
        {
            pending = crawl.Seen.Count > 0; // stopped: keep what was fetched for the person to decide on
        }
        catch (IpmsUnavailableException unavailable)
        {
            error = await SanitizeAsync(unavailable.Message);
        }
        catch (Exception unexpected)
        {
            error = Generic;
            logger.LogWarning(unexpected, "Manual MES mirror run failed.");
        }

        try
        {
            if (pending && error is null)
            {
                await context.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE mes_mirror_state SET lease_until=UTC_TIMESTAMP(3) + INTERVAL {LeaseMinutes} MINUTE WHERE id=1", CancellationToken.None);
                lock (gate)
                {
                    run.Fetched = crawl.Seen.Count;
                    run.PendingReason = run.Mode == "PAGE" ? "PAGE_FETCHED" : "STOPPED";
                    run.Phase = AwaitingConfirm;
                }
                return;
            }
            await FinishAsync(context, error, replaced);
            await WriteAuditAsync(context, actorId, new
            {
                targetName = "IPMS 资料抓取", trigger = "MANUAL", mode = run.Mode, fetched = crawl.Seen.Count, pages = crawl.Pages, error,
            }, CancellationToken.None);
        }
        catch (Exception finishing)
        {
            logger.LogWarning(finishing, "Manual MES mirror run could not record its outcome.");
        }
        lock (gate) { if (ReferenceEquals(manual, run)) manual = null; }
        run.Cts.Dispose();
    }

    private static async Task<bool> LeaseHeldAsync(YfDbContext context, CancellationToken ct)
    {
        var until = await context.MesMirrorState.AsNoTracking().Where(x => x.Id == 1).Select(x => x.LeaseUntil).SingleOrDefaultAsync(ct);
        return until is { } lease && lease > await DbClock.UtcNowAsync(context, ct, 3);
    }

    /// <summary>A pending result whose lease ran out is abandoned; the next run clears its staged rows.</summary>
    private void DiscardStalePendingResult()
    {
        lock (gate) { if (manual is { Phase: AwaitingConfirm }) manual = null; }
    }

    // ---- connection settings and test ----

    public async Task<MesMirrorSettingsResponse> SettingsAsync(CurrentUser actor, CancellationToken ct)
    {
        await AuthorizeAsync(actor, ct);
        return await BuildSettingsAsync(ct);
    }

    public async Task<MesMirrorSettingsResponse> SaveSettingsAsync(CurrentUser actor, MesMirrorSettingsRequest request, CancellationToken ct)
    {
        var url = NormalizeBaseUrl(request.BaseUrl);
        var account = NormalizeAccount(request.Account);
        var password = string.IsNullOrEmpty(request.Password) ? null : NormalizePassword(request.Password);
        var syncTime = string.IsNullOrWhiteSpace(request.SyncTime) ? null : NormalizeSyncTime(request.SyncTime);
        if (account is null && password is not null) throw ApiException.BadRequest("请先填写 IPMS 账号");
        var actorId = await AuthorizeAsync(actor, ct);
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await context.Database.OpenConnectionAsync(ct);
        await context.Database.ExecuteSqlRawAsync("INSERT IGNORE INTO mes_mirror_state(id) VALUES(1)", ct);
        var before = await context.MesMirrorState.AsNoTracking().SingleAsync(x => x.Id == 1, ct);
        var connectionChanged = (before.BaseUrl ?? "") != (url ?? "") || (before.Account ?? "") != (account ?? "") || password is not null;
        // A running fetch keeps its session; changing where or how it logs in underneath it is refused.
        if (connectionChanged && await LeaseHeldAsync(context, ct)) throw ApiException.Conflict("抓取任务进行中或等待确认时不能更改连接设定");
        // Clearing the account also clears the saved password; otherwise a new password replaces it and a blank one keeps it.
        var storedPassword = account is null ? null : password is not null ? settings.Protect(password) : before.ProtectedPassword;
        var nextSyncTime = syncTime ?? before.SyncTime;
        await context.MesMirrorState.Where(x => x.Id == 1).ExecuteUpdateAsync(x => x
            .SetProperty(r => r.BaseUrl, url).SetProperty(r => r.Account, account)
            .SetProperty(r => r.ProtectedPassword, storedPassword).SetProperty(r => r.SyncTime, nextSyncTime), ct);
        var passwordChange = storedPassword == before.ProtectedPassword ? "" : storedPassword is null ? "已清除" : "已更改";
        var changes = new List<object>();
        void Track(string field, string label, string? from, string? to)
        {
            if ((from ?? "") != (to ?? "")) changes.Add(new { field, label, before = from ?? "", after = to ?? "" });
        }
        Track("baseUrl", "IPMS 地址", before.BaseUrl, url);
        Track("account", "IPMS 账号", before.Account, account);
        Track("password", "IPMS 密码", "", passwordChange); // the password itself is never recorded
        Track("syncTime", "每日抓取时间", before.SyncTime, nextSyncTime);
        if (changes.Count > 0)
            await audit.WriteAsync(context.Database.Connection(), null, actorId, "OUTSOURCE_MES_MIRROR_SETTINGS", "mes_mirror_state", null,
                new { targetName = "IPMS 连接设定", changes }, null, ct);
        return await BuildSettingsAsync(ct);
    }

    public async Task<MesTestResult> TestAsync(CurrentUser actor, MesMirrorTestRequest request, CancellationToken ct)
    {
        var url = NormalizeBaseUrl(request.BaseUrl);
        var account = NormalizeAccount(request.Account);
        var password = string.IsNullOrEmpty(request.Password) ? null : NormalizePassword(request.Password);
        await AuthorizeAsync(actor, ct);
        await using (var context = await dbFactory.CreateDbContextAsync(ct))
        {
            await context.Database.OpenConnectionAsync(ct);
            // A login from here must not knock a running fetch out of its session.
            if (await LeaseHeldAsync(context, ct)) throw ApiException.Conflict("抓取任务进行中或等待确认时不能测试连接");
        }
        var current = await settings.ConnectionAsync(ct);
        // A typed account brings its own password; the saved password only goes with the saved account.
        var testAccount = account ?? current.Account;
        var testPassword = password ?? (testAccount == current.Account ? current.Password : "");
        return await source.TestAsync(new MesConnection(url ?? current.BaseUrl, testAccount, testPassword), ct);
    }

    /// <summary>Blank clears the address; otherwise a complete http or https address without credentials, query or fragment.</summary>
    internal static string? NormalizeBaseUrl(string? raw)
    {
        var text = raw?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        if (text.Length > 256 || !Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || uri.Host.Length == 0 || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0
            || text.Contains('#', StringComparison.Ordinal))
            throw ApiException.BadRequest("IPMS 地址需为 http 或 https 开头的完整地址，不含账号密码");
        var normalized = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return normalized.Length > 256 ? throw ApiException.BadRequest("IPMS 地址过长") : normalized;
    }

    private static string? NormalizeAccount(string? raw)
    {
        var text = raw?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        return text.Length > 128 || text.Any(char.IsControl) ? throw ApiException.BadRequest("IPMS 账号最多 128 个字符") : text;
    }

    private static string NormalizePassword(string raw) =>
        raw.Length > 256 || raw.Any(char.IsControl) ? throw ApiException.BadRequest("IPMS 密码最多 256 个字符") : raw;

    private static string NormalizeSyncTime(string raw)
    {
        var text = raw.Trim();
        return TimeOnly.TryParseExact(text, "HH:mm", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out _) ? text : throw ApiException.BadRequest("每日抓取时间格式应为 HH:mm");
    }

    private async Task<MesMirrorSettingsResponse> BuildSettingsAsync(CancellationToken ct)
    {
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await context.Database.ExecuteSqlRawAsync("INSERT IGNORE INTO mes_mirror_state(id) VALUES(1)", ct);
        var saved = await context.MesMirrorState.AsNoTracking().Where(x => x.Id == 1)
            .Select(x => new { x.BaseUrl, x.SyncTime, x.ScheduleEnabled }).SingleAsync(ct);
        var configured = string.IsNullOrWhiteSpace(options.Ipms.MesBaseUrl) ? null : options.Ipms.MesBaseUrl;
        var current = await settings.ConnectionAsync(ct);
        return new(current.BaseUrl ?? "", configured, !string.IsNullOrWhiteSpace(saved.BaseUrl), current.Account,
            !string.IsNullOrEmpty(current.Password), current.Complete, SyncTimeOf(saved.SyncTime), saved.ScheduleEnabled);
    }

    /// <summary>Starts or stops the daily fetch; a fetch already under way finishes, and manual fetches work either way.</summary>
    public async Task<MesMirrorSettingsResponse> SetScheduleAsync(CurrentUser actor, ScheduleSwitchRequest request, CancellationToken ct)
    {
        var actorId = await AuthorizeAsync(actor, ct);
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await context.Database.OpenConnectionAsync(ct);
        await context.Database.ExecuteSqlRawAsync("INSERT IGNORE INTO mes_mirror_state(id) VALUES(1)", ct);
        var before = await context.MesMirrorState.AsNoTracking().Where(x => x.Id == 1).Select(x => x.ScheduleEnabled).SingleAsync(ct);
        if (before != request.Enabled)
        {
            await context.MesMirrorState.Where(x => x.Id == 1).ExecuteUpdateAsync(x => x.SetProperty(r => r.ScheduleEnabled, request.Enabled), ct);
            await audit.WriteAsync(context.Database.Connection(), null, actorId, "OUTSOURCE_MES_MIRROR_SCHEDULE", "mes_mirror_state", null, new
            {
                targetName = "IPMS 定时抓取",
                changes = AuditChange.OnlyChanged(new AuditChange("scheduleEnabled", "定时任务", before ? "开启" : "停止", request.Enabled ? "开启" : "停止")),
            }, null, ct);
        }
        return await BuildSettingsAsync(ct);
    }

    private string SyncTimeOf(string? saved) => string.IsNullOrWhiteSpace(saved) ? options.Ipms.MesSyncTime : saved;

    private async Task<MesMirrorStatusResponse> BuildStatusAsync(CancellationToken ct)
    {
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await context.Database.ExecuteSqlRawAsync("INSERT IGNORE INTO mes_mirror_state(id) VALUES(1)", ct);
        var state = await context.MesMirrorState.AsNoTracking().SingleAsync(x => x.Id == 1, ct);
        var now = await DbClock.UtcNowAsync(context, ct, 3);
        var leaseHeld = state.LeaseUntil is { } lease && lease > now;
        var mirrorCount = await context.MesWorkOrders.CountAsync(ct);

        ManualRun? run;
        lock (gate)
        {
            if (manual is { Phase: AwaitingConfirm } && !leaseHeld) manual = null;
            run = manual;
        }
        string phase = Idle, runMode = null!;
        int? page = null, total = null;
        int fetched = 0, pages = 0, size = options.Ipms.MesPageSize;
        string? reason = null;
        DateTime? startedAt = null;
        if (run is not null)
        {
            lock (gate)
            {
                phase = run.Phase; runMode = run.Mode; page = run.Page; size = run.PageSize; fetched = run.Fetched; pages = run.Pages;
                total = run.Total >= 0 ? run.Total : null; reason = run.PendingReason; startedAt = run.StartedAt;
            }
        }
        var preview = phase == AwaitingConfirm
            ? (await context.MesWorkOrdersStaging.AsNoTracking().OrderBy(x => x.WorkOrderNo).Take(PreviewRows)
                .Select(x => new MesMirrorPreviewRow(x.WorkOrderNo, x.CustomerName, x.DeviceModels)).ToArrayAsync(ct))
            : [];
        return new(phase, run is null ? null : runMode, page, size, fetched, pages, total, phase == AwaitingConfirm ? reason : null,
            startedAt is { } started ? DateTime.SpecifyKind(started, DateTimeKind.Utc) : null,
            Utc(state.LastFinishedAt), Utc(state.LastSuccessAt), state.LastError, state.LastTrigger, state.LastCount, mirrorCount, preview);
    }

    private static DateTime? Utc(DateTime? value) => value is { } v ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : null;

    private async Task<ulong> AuthorizeAsync(CurrentUser actor, CancellationToken ct)
    {
        AccessService.RequireInternal(actor);
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await context.Database.OpenConnectionAsync(ct);
        var connection = context.Database.Connection();
        actor = await AccessService.ReadActorAsync(connection, null, actor, ct);
        AccessService.RequireInternal(actor);
        await AccessService.RequirePermissionAsync(connection, null, actor, OutsourcePermissions.Settings, ct);
        return actor.Id;
    }

    // ---- schedule ----

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.WorkerEnabled || options.Ipms.Mode == IpmsOptions.FakeMode) return;
        using var timer = new PeriodicTimer(ScanInterval);
        do
        {
            try
            {
                await using var context = await dbFactory.CreateDbContextAsync(stoppingToken);
                // Nothing runs until the IPMS connection is set up on the settings page (or in configuration).
                if (!(await settings.ConnectionAsync(stoppingToken)).Complete) continue;
                await context.Database.ExecuteSqlRawAsync("INSERT IGNORE INTO mes_mirror_state(id) VALUES(1)", stoppingToken);
                var state = await context.MesMirrorState.AsNoTracking().Where(x => x.Id == 1)
                    .Select(x => new { x.ScheduledFor, x.SyncTime, x.ScheduleEnabled }).SingleAsync(stoppingToken);
                if (!state.ScheduleEnabled) continue;
                var now = await DbClock.UtcNowAsync(context, stoppingToken, 3);
                if (!OutsourceIpmsSyncService.IsDue(SyncTimeOf(state.SyncTime), state.ScheduledFor, now, out var day)) continue;
                var outcome = await RunAsync("SCHEDULED", day, stoppingToken);
                if (outcome is not null)
                    logger.LogInformation("IPMS fetch: {Fetched} work orders in {Pages} pages, error {Error}.", outcome.Fetched, outcome.Pages, outcome.Error);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogWarning(error, "MES mirror scheduler failed; retrying next cycle."); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
