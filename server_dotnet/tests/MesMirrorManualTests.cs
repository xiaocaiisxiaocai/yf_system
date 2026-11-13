using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Outsource.Ipms;
using Yf.Api.Modules.Outsource.Ipms.Mes;

namespace Yf.Api.Tests;

/// <summary>Manual MES mirror runs: single page, full run, stop and the confirm-or-discard decision (设计 → 手动抓取).</summary>
public sealed class MesMirrorManualTests
{
    private sealed class FakeMes : IMesSource
    {
        public List<string> WorkOrders { get; } = [];
        public int HangOnPage { get; set; }
        public int FailPage { get; set; }
        public MesConnection? Tested { get; private set; }
        public string? TestedUrl => Tested?.BaseUrl;
        public int TestCalls { get; private set; }

        public Task<MesTestResult> TestAsync(MesConnection connection, CancellationToken ct)
        {
            Tested = connection;
            TestCalls++;
            return Task.FromResult(new MesTestResult(true, "连接成功，MES 共 5 笔", 5, 12));
        }

        public async Task<MesPage> FetchPageAsync(int page, int pageSize, CancellationToken ct)
        {
            if (page == HangOnPage) await Task.Delay(Timeout.Infinite, ct);
            if (page == FailPage) throw new IpmsUnavailableException("MES 暂时无法连接，请稍后再试");
            var rows = WorkOrders.Skip((page - 1) * pageSize).Take(pageSize)
                .Select(no => new MesRow(no, JsonSerializer.Serialize(new { workOrder = no, customerName = "客户" + no, deviceModels = "M-" + no })))
                .ToArray();
            return new(WorkOrders.Count, rows);
        }
    }

    private sealed record Fixture(SchemaShapeTests.SchemaDatabaseScope Database, FakeMes Source, MesMirrorSyncService Service,
        CurrentUser Admin, CurrentUser Viewer);

    private static async Task<Fixture> CreateAsync(string name, int total, CancellationToken ct)
    {
        var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync(name, ct);
        await database.InitializeBusinessFixtureAsync(ct);
        await database.ExecuteAsync("""
            INSERT INTO roles(id,name,is_built_in,status,created_at,updated_at) VALUES
              (80,'外协设定',0,'ACTIVE',UTC_TIMESTAMP(),UTC_TIMESTAMP()),
              (81,'外协查看',0,'ACTIVE',UTC_TIMESTAMP(),UTC_TIMESTAMP());
            INSERT INTO role_permissions(role_id,permission_id) SELECT 80,id FROM permissions WHERE code LIKE 'outsource%';
            INSERT INTO role_permissions(role_id,permission_id) SELECT 81,id FROM permissions WHERE code IN ('outsource','outsource:view');
            INSERT INTO users(id,employee_no,password_hash,real_name,email,user_type,supplier_id,status,must_change_password,failed_login_attempts,created_at,updated_at) VALUES
              (9821,'mes_admin','unused','设定','admin@example.invalid','INTERNAL',NULL,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
              (9822,'mes_viewer','unused','查看','viewer@example.invalid','INTERNAL',NULL,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
            INSERT INTO user_roles(user_id,role_id) VALUES(9821,80),(9822,81);
            """, ct);
        // A manual run keeps its own connection open while the test polls the status, so one pooled connection is not enough.
        database.Options.ConnectionString = new MySqlConnector.MySqlConnectionStringBuilder(database.Options.ConnectionString) { MaximumPoolSize = 8 }.ConnectionString;
        var source = new FakeMes();
        source.WorkOrders.AddRange(Enumerable.Range(1, total).Select(i => "A" + i));
        var options = new AppOptions
        {
            Ipms = new IpmsOptions
            {
                MesBaseUrl = "http://127.0.0.1:6001", MesAccount = "acct-1", MesPassword = "s3cret-PW",
                MesPageSize = 2, MesPageDelaySeconds = 0,
            },
            JwtSecret = "test-secret-for-the-ipms-password-0123456789",
        };
        var factory = EfTestSupport.DbContextFactory(database.Options);
        var settings = new DbMesSettings(factory, options);
        var service = new MesMirrorSyncService(factory, new AuditService([]), source, settings, options, NullLogger<MesMirrorSyncService>.Instance);
        return new(database, source, service, new CurrentUser(9821, "mes_admin", "INTERNAL", null), new CurrentUser(9822, "mes_viewer", "INTERNAL", null));
    }

    private static MesMirrorSettingsRequest Address(string? url) => new(url, null, null, null);

    private static async Task<MesMirrorStatusResponse> WaitAsync(Fixture f, Func<MesMirrorStatusResponse, bool> done, CancellationToken ct)
    {
        for (var i = 0; i < 400; i++)
        {
            var status = await f.Service.StatusAsync(f.Admin, ct);
            if (done(status)) return status;
            await Task.Delay(50, ct);
        }
        throw new TimeoutException("MES mirror status did not reach the expected state: " + JsonSerializer.Serialize(await f.Service.StatusAsync(f.Admin, ct)));
    }

    private static async Task SeedAsync(Fixture f, CancellationToken ct, params (string No, string Customer)[] rows)
    {
        foreach (var (no, customer) in rows)
            await f.Database.ExecuteAsync($"INSERT INTO mes_work_orders(work_order_no,batch_id,fetched_at,customer_name,raw_json) VALUES('{no}','old',UTC_TIMESTAMP(3),'{customer}','{{}}')", ct);
    }

    private static async Task<string[]> MirrorAsync(Fixture f, CancellationToken ct)
    {
        await using var connection = await f.Database.Database.OpenAsync(ct);
        return (await connection.QueryAsync<string>("SELECT work_order_no FROM mes_work_orders ORDER BY work_order_no")).ToArray();
    }

    private static async Task<int> StagingAsync(Fixture f, CancellationToken ct)
    {
        await using var connection = await f.Database.Database.OpenAsync(ct);
        return await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM mes_work_orders_staging");
    }

    [Fact(Timeout = 180_000)]
    public async Task ThePreviewListsRowsInTheOrderMesReturnedThem()
    {
        var ct = TestContext.Current.CancellationToken;
        var f = await CreateAsync("mes_manual_order", 0, ct);
        await using var _ = f.Database;
        f.Source.WorkOrders.AddRange(["21074672", "21058021", "22001223", "21067365"]);

        await f.Service.StartAsync(f.Admin, new("PAGE", 1, 4), ct);
        var pending = await WaitAsync(f, s => s.Phase == "AWAITING_CONFIRM", ct);

        Assert.Equal(["21074672", "21058021", "22001223", "21067365"], pending.Preview.Select(x => x.WorkOrderNo));
        await f.Service.ConfirmAsync(f.Admin, new(false), ct);
    }

    [Fact(Timeout = 180_000)]
    public async Task ALeaseLeftByAPreviousProcessIsReleasedAtStartup()
    {
        var ct = TestContext.Current.CancellationToken;
        var f = await CreateAsync("mes_manual_restart", 2, ct);
        await using var _ = f.Database;
        await f.Database.ExecuteAsync("INSERT INTO mes_mirror_state(id,lease_until) VALUES(1, UTC_TIMESTAMP(3) + INTERVAL 30 MINUTE)", ct);
        await f.Database.ExecuteAsync("INSERT INTO mes_work_orders_staging(work_order_no,batch_id,fetched_at,raw_json,seq) VALUES('X1','b',UTC_TIMESTAMP(3),'{}',0)", ct);

        await f.Service.StartAsync(ct);
        await f.Service.StopAsync(ct);

        Assert.Equal(0, await StagingAsync(f, ct));
        var started = await f.Service.StartAsync(f.Admin, new("ALL", null, 2), ct);
        Assert.Contains(started.Phase, new[] { "RUNNING", "IDLE" });
        await WaitAsync(f, s => s.Phase == "IDLE", ct);
    }

    [Fact(Timeout = 180_000)]
    public async Task PageRunWaitsForConfirmationAndMergeKeepsOtherRows()
    {
        var ct = TestContext.Current.CancellationToken;
        var f = await CreateAsync("mes_manual_page", 5, ct);
        await using var _ = f.Database;
        await SeedAsync(f, ct, ("OLD1", "旧"), ("OLD2", "旧"), ("A3", "旧"));

        var started = await f.Service.StartAsync(f.Admin, new("PAGE", 2, 2), ct);
        Assert.Contains(started.Phase, new[] { "RUNNING", "AWAITING_CONFIRM" });
        var pending = await WaitAsync(f, s => s.Phase == "AWAITING_CONFIRM", ct);

        Assert.Equal("PAGE_FETCHED", pending.PendingReason);
        Assert.Equal(["A3", "A4"], pending.Preview.Select(x => x.WorkOrderNo));
        Assert.Equal(["A3", "OLD1", "OLD2"], await MirrorAsync(f, ct));

        var merged = await f.Service.ConfirmAsync(f.Admin, new(true), ct);

        Assert.Equal(("IDLE", 4), (merged.Phase, merged.MirrorCount));
        Assert.Equal(["A3", "A4", "OLD1", "OLD2"], await MirrorAsync(f, ct));
        await using (var connection = await f.Database.Database.OpenAsync(ct))
            Assert.Equal("客户A3", await connection.ExecuteScalarAsync<string>("SELECT customer_name FROM mes_work_orders WHERE work_order_no='A3'"));
        Assert.Equal(0, await StagingAsync(f, ct));
        Assert.Equal(pending.LastSuccessAt, merged.LastSuccessAt);
    }

    [Fact(Timeout = 180_000)]
    public async Task DiscardingKeepsTheMirrorAndClearsStaging()
    {
        var ct = TestContext.Current.CancellationToken;
        var f = await CreateAsync("mes_manual_discard", 5, ct);
        await using var _ = f.Database;
        await SeedAsync(f, ct, ("OLD1", "旧"));
        await f.Service.StartAsync(f.Admin, new("PAGE", 1, 2), ct);
        await WaitAsync(f, s => s.Phase == "AWAITING_CONFIRM", ct);

        var status = await f.Service.ConfirmAsync(f.Admin, new(false), ct);

        Assert.Equal("IDLE", status.Phase);
        Assert.Equal(["OLD1"], await MirrorAsync(f, ct));
        Assert.Equal(0, await StagingAsync(f, ct));
        Assert.Empty(status.Preview);
    }

    [Fact(Timeout = 180_000)]
    public async Task StoppingAFullRunAsksBeforeUpdatingAndMergeDeletesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var f = await CreateAsync("mes_manual_stop", 6, ct);
        await using var _ = f.Database;
        await SeedAsync(f, ct, ("OLD1", "旧"), ("OLD2", "旧"));
        f.Source.HangOnPage = 3;

        await f.Service.StartAsync(f.Admin, new("ALL", null, 2), ct);
        var running = await WaitAsync(f, s => s.Phase == "RUNNING" && s.Pages == 2, ct);
        Assert.Equal((4, 6), (running.Fetched, running.Total));

        var stopped = await f.Service.StopAsync(f.Admin, ct);

        Assert.Equal(("AWAITING_CONFIRM", "STOPPED", 4), (stopped.Phase, stopped.PendingReason, stopped.Fetched));
        Assert.Equal(["A1", "A2", "A3", "A4"], stopped.Preview.Select(x => x.WorkOrderNo));
        Assert.Equal(["OLD1", "OLD2"], await MirrorAsync(f, ct));

        await f.Service.ConfirmAsync(f.Admin, new(true), ct);
        Assert.Equal(["A1", "A2", "A3", "A4", "OLD1", "OLD2"], await MirrorAsync(f, ct));
        Assert.Equal(0, await StagingAsync(f, ct));
    }

    [Fact(Timeout = 180_000)]
    public async Task StoppingBeforeAnyPageWasFetchedReturnsToIdle()
    {
        var ct = TestContext.Current.CancellationToken;
        var f = await CreateAsync("mes_manual_stop_early", 4, ct);
        await using var _ = f.Database;
        f.Source.HangOnPage = 1;
        await f.Service.StartAsync(f.Admin, new("ALL", null, 2), ct);
        await WaitAsync(f, s => s.Phase == "RUNNING", ct);

        var stopped = await f.Service.StopAsync(f.Admin, ct);

        Assert.Equal("IDLE", stopped.Phase);
        Assert.Empty(stopped.Preview);
        Assert.Equal(0, await StagingAsync(f, ct));
        Assert.Null(stopped.LastError);
    }

    [Fact(Timeout = 180_000)]
    public async Task CompletingAFullRunReplacesTheMirrorWithoutConfirmation()
    {
        var ct = TestContext.Current.CancellationToken;
        var f = await CreateAsync("mes_manual_full", 5, ct);
        await using var _ = f.Database;
        await SeedAsync(f, ct, ("OLD1", "旧"));

        await f.Service.StartAsync(f.Admin, new("ALL", null, 2), ct);
        var done = await WaitAsync(f, s => s.Phase == "IDLE", ct);

        Assert.Equal(["A1", "A2", "A3", "A4", "A5"], await MirrorAsync(f, ct));
        Assert.NotNull(done.LastSuccessAt);
        Assert.Equal((5, "MANUAL"), (done.LastCount, done.LastTrigger));
    }

    [Fact(Timeout = 180_000)]
    public async Task OnlyOneRunAtATime()
    {
        var ct = TestContext.Current.CancellationToken;
        var f = await CreateAsync("mes_manual_single", 4, ct);
        await using var _ = f.Database;
        f.Source.HangOnPage = 1;
        await f.Service.StartAsync(f.Admin, new("ALL", null, 2), ct);
        await WaitAsync(f, s => s.Phase == "RUNNING", ct);

        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => f.Service.StartAsync(f.Admin, new("ALL", null, 2), ct))).Status);
        Assert.Null(await f.Service.RunAsync("SCHEDULED", new DateOnly(2026, 11, 13), ct));
        await f.Service.StopAsync(f.Admin, ct);

        f.Source.HangOnPage = 0;
        await f.Service.StartAsync(f.Admin, new("PAGE", 1, 2), ct);
        await WaitAsync(f, s => s.Phase == "AWAITING_CONFIRM", ct);
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => f.Service.StartAsync(f.Admin, new("PAGE", 1, 2), ct))).Status);
        Assert.Null(await f.Service.RunAsync("SCHEDULED", new DateOnly(2026, 11, 14), ct));
        await f.Service.ConfirmAsync(f.Admin, new(false), ct);
    }

    [Fact(Timeout = 180_000)]
    public async Task ExpiredPendingResultIsDiscardedByTheNextRun()
    {
        var ct = TestContext.Current.CancellationToken;
        var f = await CreateAsync("mes_manual_expired", 5, ct);
        await using var _ = f.Database;
        await f.Service.StartAsync(f.Admin, new("PAGE", 2, 2), ct);
        await WaitAsync(f, s => s.Phase == "AWAITING_CONFIRM", ct);
        await f.Database.ExecuteAsync("UPDATE mes_mirror_state SET lease_until=UTC_TIMESTAMP(3) - INTERVAL 1 MINUTE WHERE id=1", ct);
        Assert.Equal("IDLE", (await f.Service.StatusAsync(f.Admin, ct)).Phase);

        await f.Service.StartAsync(f.Admin, new("PAGE", 1, 2), ct);
        var pending = await WaitAsync(f, s => s.Phase == "AWAITING_CONFIRM", ct);

        Assert.Equal(["A1", "A2"], pending.Preview.Select(x => x.WorkOrderNo));
        await f.Service.ConfirmAsync(f.Admin, new(false), ct);
    }

    [Fact(Timeout = 180_000)]
    public async Task PageBeyondTheLastPageReturnsToIdleWithoutAConfirmation()
    {
        var ct = TestContext.Current.CancellationToken;
        var f = await CreateAsync("mes_manual_beyond", 5, ct);
        await using var _ = f.Database;
        await f.Service.StartAsync(f.Admin, new("PAGE", 999, 2), ct);
        var done = await WaitAsync(f, s => s.Phase == "IDLE", ct);
        Assert.Null(done.LastError);
        Assert.Empty(done.Preview);
        Assert.Equal(0, await StagingAsync(f, ct));
    }

    [Fact(Timeout = 180_000)]
    public async Task FailedManualRunRecordsTheErrorAndKeepsTheMirror()
    {
        var ct = TestContext.Current.CancellationToken;
        var f = await CreateAsync("mes_manual_failed", 6, ct);
        await using var _ = f.Database;
        await SeedAsync(f, ct, ("OLD1", "旧"));
        f.Source.FailPage = 2;
        await f.Service.StartAsync(f.Admin, new("ALL", null, 2), ct);
        var done = await WaitAsync(f, s => s.Phase == "IDLE", ct);
        Assert.Equal("MES 暂时无法连接，请稍后再试", done.LastError);
        Assert.Equal(["OLD1"], await MirrorAsync(f, ct));
        Assert.Equal(0, await StagingAsync(f, ct));
    }

    [Theory(Timeout = 180_000)]
    [InlineData(null, null, null)]
    [InlineData("X", null, null)]
    [InlineData("PAGE", null, 2)]
    [InlineData("PAGE", 0, 2)]
    [InlineData("ALL", null, 0)]
    [InlineData("ALL", null, 201)]
    public async Task RejectsInvalidRequests(string? mode, int? page, int? pageSize)
    {
        var ct = TestContext.Current.CancellationToken;
        var f = await CreateAsync("mes_manual_invalid", 2, ct);
        await using var _ = f.Database;
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => f.Service.StartAsync(f.Admin, new(mode, page, pageSize), ct))).Status);
    }

    [Fact(Timeout = 180_000)]
    public async Task OnlyTheSettingsPermissionMayUseThem()
    {
        var ct = TestContext.Current.CancellationToken;
        var f = await CreateAsync("mes_manual_permission", 2, ct);
        await using var _ = f.Database;
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => f.Service.StatusAsync(f.Viewer, ct))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => f.Service.StartAsync(f.Viewer, new("ALL", null, 2), ct))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => f.Service.StopAsync(f.Viewer, ct))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => f.Service.ConfirmAsync(f.Viewer, new(true), ct))).Status);
    }

    [Fact(Timeout = 180_000)]
    public async Task SettingsFallBackToTheConfiguredUrlAndSaveNormalizes()
    {
        var ct = TestContext.Current.CancellationToken;
        var f = await CreateAsync("mes_settings_url", 2, ct);
        await using var _ = f.Database;

        var initial = await f.Service.SettingsAsync(f.Admin, ct);
        Assert.Equal(("http://127.0.0.1:6001", "http://127.0.0.1:6001", false, true), (initial.BaseUrl, initial.ConfigBaseUrl, initial.Saved, initial.AccountConfigured));

        var saved = await f.Service.SaveSettingsAsync(f.Admin, Address("  http://10.0.0.5:6001/ "), ct);
        Assert.Equal(("http://10.0.0.5:6001", true), (saved.BaseUrl, saved.Saved));
        Assert.Equal("http://10.0.0.5:6001", (await f.Service.SettingsAsync(f.Admin, ct)).BaseUrl);

        var cleared = await f.Service.SaveSettingsAsync(f.Admin, Address(" "), ct);
        Assert.Equal(("http://127.0.0.1:6001", false), (cleared.BaseUrl, cleared.Saved));
    }

    [Theory(Timeout = 180_000)]
    [InlineData("ftp://10.0.0.5")]
    [InlineData("http://user:pw@10.0.0.5:6001")]
    [InlineData("not a url")]
    [InlineData("http://10.0.0.5:6001/#fragment")]
    [InlineData("//10.0.0.5")]
    public async Task RejectsInvalidAddresses(string url)
    {
        var ct = TestContext.Current.CancellationToken;
        var f = await CreateAsync("mes_settings_invalid", 2, ct);
        await using var _ = f.Database;
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => f.Service.SaveSettingsAsync(f.Admin, Address(url), ct))).Status);
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => f.Service.TestAsync(f.Admin, new(url, null, null), ct))).Status);
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => f.Service.SaveSettingsAsync(f.Admin, Address("http://x/" + new string('a', 300)), ct))).Status);
    }

    [Fact(Timeout = 180_000)]
    public async Task TestPassesTheNormalizedAddressToTheSourceAndWritesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var f = await CreateAsync("mes_settings_test", 5, ct);
        await using var _ = f.Database;
        await SeedAsync(f, ct, ("OLD1", "旧"));

        var result = await f.Service.TestAsync(f.Admin, new("http://10.0.0.5:6001/", null, null), ct);
        Assert.Equal((true, 5), (result.Ok, result.Total));
        Assert.Equal("http://10.0.0.5:6001", f.Source.TestedUrl);

        await f.Service.TestAsync(f.Admin, new(null, null, null), ct);
        Assert.Equal(("http://127.0.0.1:6001", "acct-1", "s3cret-PW"), (f.Source.Tested!.BaseUrl, f.Source.Tested.Account, f.Source.Tested.Password));
        Assert.Equal(["OLD1"], await MirrorAsync(f, ct));
        Assert.Equal(0, await StagingAsync(f, ct));
        Assert.False((await f.Service.SettingsAsync(f.Admin, ct)).Saved); // testing never saves the address
    }

    [Fact(Timeout = 180_000)]
    public async Task SettingsAndTestAreRefusedWhileARunIsActiveOrWaiting()
    {
        var ct = TestContext.Current.CancellationToken;
        var f = await CreateAsync("mes_settings_busy", 4, ct);
        await using var _ = f.Database;
        f.Source.HangOnPage = 1;
        await f.Service.StartAsync(f.Admin, new("ALL", null, 2), ct);
        await WaitAsync(f, s => s.Phase == "RUNNING", ct);

        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => f.Service.SaveSettingsAsync(f.Admin, Address("http://10.0.0.5:6001"), ct))).Status);
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => f.Service.TestAsync(f.Admin, new(null, null, null), ct))).Status);
        Assert.Equal(0, f.Source.TestCalls);
        await f.Service.StopAsync(f.Admin, ct);

        f.Source.HangOnPage = 0;
        await f.Service.StartAsync(f.Admin, new("PAGE", 1, 2), ct);
        await WaitAsync(f, s => s.Phase == "AWAITING_CONFIRM", ct);
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => f.Service.SaveSettingsAsync(f.Admin, Address("http://10.0.0.5:6001"), ct))).Status);
        await f.Service.ConfirmAsync(f.Admin, new(false), ct);
        Assert.True((await f.Service.TestAsync(f.Admin, new(null, null, null), ct)).Ok);
    }

    [Fact(Timeout = 180_000)]
    public async Task OnlyTheSettingsPermissionMayReadSaveOrTest()
    {
        var ct = TestContext.Current.CancellationToken;
        var f = await CreateAsync("mes_settings_perm", 2, ct);
        await using var _ = f.Database;
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => f.Service.SettingsAsync(f.Viewer, ct))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => f.Service.SaveSettingsAsync(f.Viewer, Address("http://10.0.0.5:6001"), ct))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => f.Service.TestAsync(f.Viewer, new(null, null, null), ct))).Status);
    }

    [Fact(Timeout = 180_000)]
    public async Task ChangingTheAddressWritesAnAuditWithoutCredentials()
    {
        var ct = TestContext.Current.CancellationToken;
        var f = await CreateAsync("mes_settings_audit", 2, ct);
        await using var _ = f.Database;
        await f.Service.SaveSettingsAsync(f.Admin, Address("http://10.0.0.5:6001"), ct);
        await f.Service.SaveSettingsAsync(f.Admin, Address("http://10.0.0.5:6001"), ct); // unchanged: no second entry
        await using var connection = await f.Database.Database.OpenAsync(ct);
        var details = (await connection.QueryAsync<string>("SELECT CAST(detail AS CHAR) FROM audit_logs WHERE action='OUTSOURCE_MES_MIRROR_SETTINGS'")).ToArray();
        var detail = Assert.Single(details);
        Assert.Contains("http://10.0.0.5:6001", detail);
        Assert.DoesNotContain("s3cret-PW", detail);
        Assert.DoesNotContain("acct-1", detail);
    }

    [Fact(Timeout = 180_000)]
    public async Task AccountAndPasswordAreSetOnThePageAndThePasswordIsStoredEncrypted()
    {
        var ct = TestContext.Current.CancellationToken;
        var f = await CreateAsync("mes_settings_account", 2, ct);
        await using var _ = f.Database;

        var saved = await f.Service.SaveSettingsAsync(f.Admin, new("http://10.0.0.5:6001", "page-user", "page-PW-123", "04:15"), ct);
        Assert.Equal(("page-user", true, true, "04:15"), (saved.Account, saved.PasswordSet, saved.AccountConfigured, saved.SyncTime));
        await using (var connection = await f.Database.Database.OpenAsync(ct))
        {
            var stored = await connection.QuerySingleAsync<(string Account, string Password)>("SELECT account, protected_password FROM mes_mirror_state WHERE id=1");
            Assert.Equal("page-user", stored.Account);
            Assert.StartsWith("v1.", stored.Password);
            Assert.DoesNotContain("page-PW-123", stored.Password);
        }

        // Testing with blank fields uses what is saved, password included.
        await f.Service.TestAsync(f.Admin, new(null, null, null), ct);
        Assert.Equal(("http://10.0.0.5:6001", "page-user", "page-PW-123"), (f.Source.Tested!.BaseUrl, f.Source.Tested.Account, f.Source.Tested.Password));

        // A blank password keeps the saved one; another typed account does not borrow it.
        await f.Service.SaveSettingsAsync(f.Admin, new("http://10.0.0.5:6001", "page-user", null, null), ct);
        await f.Service.TestAsync(f.Admin, new(null, "someone-else", null), ct);
        Assert.Equal(("someone-else", ""), (f.Source.Tested!.Account, f.Source.Tested.Password));
        await f.Service.TestAsync(f.Admin, new(null, null, null), ct);
        Assert.Equal("page-PW-123", f.Source.Tested!.Password);

        // A password needs an account; clearing the account clears the saved password and the configured ones apply again.
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => f.Service.SaveSettingsAsync(f.Admin, new(null, null, "x", null), ct))).Status);
        var cleared = await f.Service.SaveSettingsAsync(f.Admin, new(null, null, null, null), ct);
        Assert.Equal(("http://127.0.0.1:6001", "acct-1", "04:15"), (cleared.BaseUrl, cleared.Account, cleared.SyncTime));
        await using (var connection = await f.Database.Database.OpenAsync(ct))
            Assert.Null(await connection.ExecuteScalarAsync<string?>("SELECT protected_password FROM mes_mirror_state WHERE id=1"));
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => f.Service.SaveSettingsAsync(f.Admin, new(null, null, null, "25:00"), ct))).Status);
    }

    [Fact(Timeout = 180_000)]
    public async Task ConnectionChangesAreAuditedWithoutThePassword()
    {
        var ct = TestContext.Current.CancellationToken;
        var f = await CreateAsync("mes_settings_account_audit", 2, ct);
        await using var _ = f.Database;
        await f.Service.SaveSettingsAsync(f.Admin, new("http://10.0.0.5:6001", "page-user", "page-PW-123", null), ct);
        await using var connection = await f.Database.Database.OpenAsync(ct);
        var detail = Assert.Single(await connection.QueryAsync<string>("SELECT CAST(detail AS CHAR) FROM audit_logs WHERE action='OUTSOURCE_MES_MIRROR_SETTINGS'"));
        Assert.Contains("page-user", detail);
        Assert.Contains("已更改", detail);
        Assert.DoesNotContain("page-PW-123", detail);
        Assert.DoesNotContain("v1.", detail);
    }

    [Fact(Timeout = 180_000)]
    public async Task TheDailyFetchCanBeStoppedAndStartedAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        var f = await CreateAsync("mes_schedule_switch", 2, ct);
        await using var _ = f.Database;
        Assert.True((await f.Service.SettingsAsync(f.Admin, ct)).ScheduleEnabled);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => f.Service.SetScheduleAsync(f.Viewer, new(false), ct))).Status);

        Assert.False((await f.Service.SetScheduleAsync(f.Admin, new(false), ct)).ScheduleEnabled);
        await f.Service.SetScheduleAsync(f.Admin, new(false), ct); // unchanged: no second audit entry
        Assert.True((await f.Service.SetScheduleAsync(f.Admin, new(true), ct)).ScheduleEnabled);
        await using var connection = await f.Database.Database.OpenAsync(ct);
        Assert.Equal(2, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM audit_logs WHERE action='OUTSOURCE_MES_MIRROR_SCHEDULE'"));
    }

    [Fact(Timeout = 180_000)]
    public async Task ConfirmWithoutAPendingResultIsRejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var f = await CreateAsync("mes_manual_noconfirm", 2, ct);
        await using var _ = f.Database;
        var error = await Assert.ThrowsAsync<ApiException>(() => f.Service.ConfirmAsync(f.Admin, new(true), ct));
        Assert.Equal((409, "没有等待确认的抓取结果"), (error.Status, error.Message));
    }
}
