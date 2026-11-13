using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Outsource.Ipms;
using Yf.Api.Modules.Outsource.Ipms.Mes;

namespace Yf.Api.Tests;

/// <summary>Daily MES mirror: staged fetch, validation and whole-table replacement (设计 → 抓取流程).</summary>
public sealed class MesMirrorSyncTests
{
    private const string Account = "acct-1";
    private const string Password = "s3cret-PW";

    private sealed class FakeMes : IMesSource
    {
        public List<string> WorkOrders { get; } = [];
        public int? TotalOverride { get; set; }
        /// <summary>Page that always fails (0 = none).</summary>
        public int FailPage { get; set; }
        /// <summary>How many times each page fails before it answers (transient errors).</summary>
        public int TransientFailures { get; set; }
        public Func<CancellationToken, Task>? Hang { get; set; }
        public Exception? Error { get; set; }
        public int Calls { get; private set; }
        private readonly Dictionary<int, int> failed = [];

        public Task<MesTestResult> TestAsync(string? baseUrl, CancellationToken ct) => Task.FromResult(new MesTestResult(true, "ok", Rows(), 0));

        private int Rows() => WorkOrders.Count;

        public async Task<MesPage> FetchPageAsync(int page, int pageSize, CancellationToken ct)
        {
            Calls++;
            if (Hang is not null) await Hang(ct);
            if (Error is not null) throw Error;
            if (page == FailPage) throw new IpmsUnavailableException("MES 暂时无法连接，请稍后再试");
            if (failed.GetValueOrDefault(page) < TransientFailures)
            {
                failed[page] = failed.GetValueOrDefault(page) + 1;
                throw new IpmsUnavailableException("MES 暂时无法连接，请稍后再试");
            }
            var rows = WorkOrders.Skip((page - 1) * pageSize).Take(pageSize)
                .Select(no => new MesRow(no, JsonSerializer.Serialize(new { workOrder = no, customerName = "客户" + no, deviceModels = "M-" + no, deviceNum = 1 })))
                .ToArray();
            return new(TotalOverride ?? WorkOrders.Count, rows);
        }
    }

    private static AppOptions Options(bool worker = true, string mode = "MesMirror") => new()
    {
        WorkerEnabled = worker,
        Ipms = new IpmsOptions
        {
            Mode = mode, MesBaseUrl = "http://127.0.0.1:6001", MesAccount = Account, MesPassword = Password,
            MesPageSize = 2, MesPageDelaySeconds = 0,
        },
    };

    private static MesMirrorSyncService Service(SchemaShapeTests.SchemaDatabaseScope database, FakeMes source, AppOptions? options = null) =>
        new(EfTestSupport.DbContextFactory(database.Options), new AuditService([]), source, options ?? Options(),
            NullLogger<MesMirrorSyncService>.Instance);

    private static async Task<string[]> MirrorAsync(SchemaShapeTests.SchemaDatabaseScope database, CancellationToken ct)
    {
        await using var connection = await database.Database.OpenAsync(ct);
        return (await connection.QueryAsync<string>("SELECT work_order_no FROM mes_work_orders ORDER BY work_order_no")).ToArray();
    }

    private static async Task<(string? Error, DateTime? Success, int Count, DateTime? Lease)> StateAsync(
        SchemaShapeTests.SchemaDatabaseScope database, CancellationToken ct)
    {
        await using var connection = await database.Database.OpenAsync(ct);
        var row = await connection.QuerySingleAsync("SELECT last_error AS e, last_success_at AS s, last_count AS c, lease_until AS l FROM mes_mirror_state WHERE id=1");
        return ((string?)row.e, (DateTime?)row.s, (int)row.c, (DateTime?)row.l);
    }

    private static async Task<int> StagingCountAsync(SchemaShapeTests.SchemaDatabaseScope database, CancellationToken ct)
    {
        await using var connection = await database.Database.OpenAsync(ct);
        return await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM mes_work_orders_staging");
    }

    private static async Task SeedMirrorAsync(SchemaShapeTests.SchemaDatabaseScope database, CancellationToken ct, params string[] workOrders)
    {
        foreach (var no in workOrders)
            await database.ExecuteAsync($"INSERT INTO mes_work_orders(work_order_no,batch_id,fetched_at,raw_json) VALUES('{no}','old',UTC_TIMESTAMP(3),'{{}}')", ct);
    }

    [Fact(Timeout = 180_000)]
    public async Task ReplacesTheMirrorAfterAFullFetch()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("mes_mirror_ok", ct);
        await database.InitializeAsync(ct);
        await SeedMirrorAsync(database, ct, "OLD1", "OLD2", "OLD3");
        var source = new FakeMes { WorkOrders = { "A1", "A2", "A3", "A4", "A5" } };

        var outcome = await Service(database, source).RunAsync("SCHEDULED", new DateOnly(2026, 11, 13), ct);

        Assert.Equal((5, 3, (string?)null), (outcome!.Fetched, outcome.Pages, outcome.Error));
        Assert.Equal(["A1", "A2", "A3", "A4", "A5"], await MirrorAsync(database, ct));
        var state = await StateAsync(database, ct);
        Assert.Equal((5, (string?)null), (state.Count, state.Error));
        Assert.NotNull(state.Success);
        Assert.Null(state.Lease);
        Assert.Equal(0, await StagingCountAsync(database, ct));
        await using var connection = await database.Database.OpenAsync(ct);
        var rows = (await connection.QueryAsync("SELECT work_order_no AS no, customer_name AS c, device_models AS m, raw_json AS j, batch_id AS b FROM mes_work_orders")).ToList();
        Assert.Single(rows.Select(x => (string)x.b).Distinct());
        var first = rows.Single(x => (string)x.no == "A1");
        Assert.Equal(("客户A1", "M-A1"), ((string)first.c, (string)first.m));
        Assert.Equal("A1", JsonDocument.Parse((string)first.j).RootElement.GetProperty("workOrder").GetString());
    }

    [Fact(Timeout = 180_000)]
    public async Task KeepsTheOldMirrorWhenAPageKeepsFailing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("mes_mirror_fail", ct);
        await database.InitializeAsync(ct);
        await SeedMirrorAsync(database, ct, "OLD1", "OLD2");
        var ok = new FakeMes { WorkOrders = { "A1", "A2" } };
        await Service(database, ok).RunAsync("MANUAL", null, ct);
        var successAt = (await StateAsync(database, ct)).Success;

        var failing = new FakeMes { WorkOrders = { "B1", "B2", "B3", "B4", "B5" }, FailPage = 2 };
        var outcome = await Service(database, failing).RunAsync("MANUAL", null, ct);

        Assert.Equal("MES 暂时无法连接，请稍后再试", outcome!.Error);
        Assert.Equal(["A1", "A2"], await MirrorAsync(database, ct));
        var state = await StateAsync(database, ct);
        Assert.Equal(("MES 暂时无法连接，请稍后再试", successAt, 2), (state.Error, state.Success, state.Count));
        Assert.Equal(0, await StagingCountAsync(database, ct));
        Assert.Equal(1 + 3, failing.Calls); // page 1 once, page 2 tried three times (first attempt + two retries)
    }

    [Theory(Timeout = 180_000)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    public async Task RetriesAFailedPageTwiceBeforeGivingUp(int failures, bool succeeds)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("mes_mirror_retry", ct);
        await database.InitializeAsync(ct);
        var source = new FakeMes { WorkOrders = { "A1", "A2" }, TransientFailures = failures };
        var outcome = await Service(database, source).RunAsync("MANUAL", null, ct);
        Assert.Equal(succeeds, outcome!.Error is null);
        Assert.Equal(succeeds ? ["A1", "A2"] : [], await MirrorAsync(database, ct));
    }

    [Theory(Timeout = 180_000)]
    [InlineData("A1,A2,A3,A4", 5, "不一致")]
    [InlineData("A1,B1,B1,C1", 4, "重复")]
    public async Task RejectsAnIncompleteOrDuplicatedFetch(string workOrders, int total, string expectedText)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("mes_mirror_incomplete", ct);
        await database.InitializeAsync(ct);
        await SeedMirrorAsync(database, ct, "OLD1");
        var source = new FakeMes { TotalOverride = total };
        source.WorkOrders.AddRange(workOrders.Split(','));

        var outcome = await Service(database, source).RunAsync("MANUAL", null, ct);

        Assert.Contains(expectedText, outcome!.Error);
        Assert.Equal(["OLD1"], await MirrorAsync(database, ct));
        Assert.Equal(0, await StagingCountAsync(database, ct));
    }

    [Fact(Timeout = 180_000)]
    public async Task RejectsASuddenDropBelowHalfOfThePreviousSuccess()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("mes_mirror_drop", ct);
        await database.InitializeAsync(ct);
        var first = new FakeMes();
        first.WorkOrders.AddRange(Enumerable.Range(1, 10).Select(i => "W" + i));
        await Service(database, first).RunAsync("MANUAL", null, ct);
        Assert.Equal(10, (await StateAsync(database, ct)).Count);

        var shrunk = new FakeMes { WorkOrders = { "W1", "W2", "W3", "W4" } };
        var outcome = await Service(database, shrunk).RunAsync("MANUAL", null, ct);
        Assert.Contains("骤降", outcome!.Error);
        Assert.Equal(10, (await MirrorAsync(database, ct)).Length);

        var half = new FakeMes();
        half.WorkOrders.AddRange(Enumerable.Range(1, 5).Select(i => "W" + i)); // exactly 50% is accepted
        Assert.Null((await Service(database, half).RunAsync("MANUAL", null, ct))!.Error);
        Assert.Equal(5, (await MirrorAsync(database, ct)).Length);
    }

    [Fact(Timeout = 180_000)]
    public async Task ClearsStaleStagingRowsBeforeStarting()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("mes_mirror_stale", ct);
        await database.InitializeAsync(ct);
        await database.ExecuteAsync("INSERT INTO mes_work_orders_staging(work_order_no,batch_id,fetched_at,raw_json) VALUES('STALE','old',UTC_TIMESTAMP(3),'{}')", ct);
        var source = new FakeMes { WorkOrders = { "A1", "A2" } };
        await Service(database, source).RunAsync("MANUAL", null, ct);
        Assert.Equal(["A1", "A2"], await MirrorAsync(database, ct));
        Assert.Equal(0, await StagingCountAsync(database, ct));
    }

    [Fact(Timeout = 180_000)]
    public async Task OnlyOneRunHoldsTheLeaseAndAScheduledRunHappensOncePerDay()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("mes_mirror_lease", ct);
        await database.InitializeAsync(ct);
        var source = new FakeMes { WorkOrders = { "A1", "A2" } };
        var service = Service(database, source);
        var day = new DateOnly(2026, 11, 13);

        Assert.NotNull(await service.RunAsync("SCHEDULED", day, ct));
        Assert.Null(await service.RunAsync("SCHEDULED", day, ct));
        Assert.NotNull(await service.RunAsync("SCHEDULED", day.AddDays(1), ct));

        await database.ExecuteAsync("UPDATE mes_mirror_state SET lease_until=UTC_TIMESTAMP(3) + INTERVAL 5 MINUTE WHERE id=1", ct);
        Assert.Null(await service.RunAsync("MANUAL", null, ct));
    }

    [Fact(Timeout = 180_000)]
    public async Task ReleasesTheLeaseAndStagingWhenCancelled()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("mes_mirror_cancel", ct);
        await database.InitializeAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var source = new FakeMes { WorkOrders = { "A1", "A2", "A3" }, Hang = async token => { cts.CancelAfter(100); await Task.Delay(Timeout.Infinite, token); } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(database, source).RunAsync("MANUAL", null, cts.Token));
        Assert.Null((await StateAsync(database, ct)).Lease);
        Assert.Equal(0, await StagingCountAsync(database, ct));
    }

    [Fact(Timeout = 180_000)]
    public async Task ErrorsAndAuditNeverContainTheCredentials()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("mes_mirror_secret", ct);
        await database.InitializeAsync(ct);
        foreach (var error in new Exception[]
        {
            new IpmsUnavailableException($"login failed for {Account} with {Password}"),
            new InvalidOperationException($"unexpected {Account}/{Password}"),
        })
        {
            var source = new FakeMes { WorkOrders = { "A1" }, Error = error };
            var outcome = await Service(database, source).RunAsync("MANUAL", null, ct);
            Assert.NotNull(outcome!.Error);
            Assert.DoesNotContain(Account, outcome.Error);
            Assert.DoesNotContain(Password, outcome.Error);
        }
        var state = await StateAsync(database, ct);
        Assert.DoesNotContain(Password, state.Error);
        await using var connection = await database.Database.OpenAsync(ct);
        var details = (await connection.QueryAsync<string>("SELECT CAST(detail AS CHAR) FROM audit_logs WHERE action='OUTSOURCE_MES_MIRROR'")).ToArray();
        Assert.Equal(2, details.Length);
        foreach (var detail in details)
        {
            Assert.DoesNotContain(Account, detail);
            Assert.DoesNotContain(Password, detail);
        }
    }

    [Theory(Timeout = 60_000)]
    [InlineData(false, "MesMirror")]
    [InlineData(true, "")]
    [InlineData(true, "Fake")]
    public async Task DoesNotStartWhenWorkerIsDisabledOrTheModeIsNotMesMirror(bool worker, string mode)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("mes_mirror_off", ct);
        await database.InitializeAsync(ct);
        var source = new FakeMes { WorkOrders = { "A1" } };
        var options = Options(worker, mode);
        options.Ipms.FakeDataPath = Path.Combine(Path.GetTempPath(), "unused.json");
        var service = Service(database, source, options);
        await service.StartAsync(ct);
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(20), ct);
        await service.StopAsync(ct);
        Assert.Equal(0, source.Calls);
        await using var connection = await database.Database.OpenAsync(ct);
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM mes_mirror_state"));
    }
}
