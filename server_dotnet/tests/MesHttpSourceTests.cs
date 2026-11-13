using System.Net;
using System.Text;
using System.Text.Json;
using Yf.Api.Modules.Outsource.Ipms;
using Yf.Api.Modules.Outsource.Ipms.Mes;

namespace Yf.Api.Tests;

/// <summary>IPMS MES monitoring HTTP source: login with the page-set account, token reuse, re-login and response handling.</summary>
public sealed class MesHttpSourceTests
{
    private const string Account = "acct-1";
    private const string Password = "s3cret-PW";
    private const string LoginPath = "/xyj-admin/r/login";
    private const string DataPath = "/xyj-twxd-mes/r/xdSalesOrder/queryByMesMonitor";

    private sealed record Seen(string Path, string Body, string? Token, string Host);

    private sealed class StubHandler(Func<Seen, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Seen> Requests { get; } = [];
        public int Logins => Requests.Count(x => x.Path == LoginPath);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            var token = request.Headers.TryGetValues("token", out var values) ? values.Single() : null;
            var seen = new Seen(request.RequestUri!.AbsolutePath, body, token, request.RequestUri.Authority);
            Requests.Add(seen);
            return respond(seen, Requests.Count(x => x.Path == seen.Path));
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    /// <summary>The real IPMS login answer: the token sits in <c>data.token.token</c> (as the IPMS web client reads it).</summary>
    private static HttpResponseMessage LoginOk(string token) =>
        Json(JsonSerializer.Serialize(new { code = 200, msg = (string?)null, data = new { token = new { token }, branchCode = "B1" } }));

    private static HttpResponseMessage Page(int total, params string[] workOrders) => Json(JsonSerializer.Serialize(new
    {
        code = 200,
        data = new
        {
            total = total.ToString(System.Globalization.CultureInfo.InvariantCulture),
            page = 1,
            pageSize = 2,
            rows = workOrders.Select(no => new { workOrder = no, customerName = "客户", deviceNum = 1 }).ToArray(),
        },
    }));

    private sealed class StubSettings(string? url) : IMesSettings
    {
        public string? Url { get; set; } = url;
        public string Account { get; set; } = MesHttpSourceTests.Account;
        public string Password { get; set; } = MesHttpSourceTests.Password;
        public Task<MesConnection> ConnectionAsync(CancellationToken ct) => Task.FromResult(new MesConnection(Url, Account, Password));
    }

    private static IpmsOptions Options() => new();

    private static MesConnection Connection(string? url = "http://127.0.0.1:6001") => new(url, Account, Password);

    private static (MesHttpSource Source, StubHandler Handler, StubSettings Settings) CreateWithSettings(
        Func<Seen, int, HttpResponseMessage> respond, TimeSpan? timeout = null)
    {
        var handler = new StubHandler(respond);
        var settings = new StubSettings("http://127.0.0.1:6001");
        return (new MesHttpSource(new HttpClient(handler), Options(), settings, timeout), handler, settings);
    }

    private static (MesHttpSource Source, StubHandler Handler) Create(Func<Seen, int, HttpResponseMessage> respond, TimeSpan? timeout = null)
    {
        var (source, handler, _) = CreateWithSettings(respond, timeout);
        return (source, handler);
    }

    [Fact]
    public async Task FetchesAPageWithTheLoginTokenAndParsesAStringTotal()
    {
        var ct = TestContext.Current.CancellationToken;
        var (source, handler) = Create((seen, _) => seen.Path == LoginPath ? LoginOk("T1") : Page(1778, "21074672", "21074704"));

        var page = await source.FetchPageAsync(3, 2, ct);

        Assert.Equal(1778, page.Total);
        Assert.Equal(["21074672", "21074704"], page.Rows.Select(x => x.WorkOrderNo));
        Assert.Equal("客户", JsonDocument.Parse(page.Rows[0].RawJson).RootElement.GetProperty("customerName").GetString());
        Assert.Equal(1, handler.Logins);
        var login = JsonDocument.Parse(handler.Requests[0].Body).RootElement;
        Assert.Equal((Account, Password, 0), (login.GetProperty("account").GetString(), login.GetProperty("password").GetString(), login.GetProperty("usertype").GetInt32()));
        var data = handler.Requests.Single(x => x.Path == DataPath);
        Assert.Equal("T1", data.Token);
        var body = JsonDocument.Parse(data.Body).RootElement;
        Assert.Equal((3, 2, true), (body.GetProperty("pageBean").GetProperty("page").GetInt32(), body.GetProperty("pageBean").GetProperty("pageSize").GetInt32(),
            body.GetProperty("pageBean").GetProperty("showTotal").GetBoolean()));
        var properties = body.GetProperty("querys").EnumerateArray().Select(x => x.GetProperty("property").GetString()).ToArray();
        Assert.Equal(["productionStatus", "outMachineStatus"], properties);
    }

    [Fact]
    public async Task ReusesTheTokenAcrossPages()
    {
        var ct = TestContext.Current.CancellationToken;
        var (source, handler) = Create((seen, _) => seen.Path == LoginPath ? LoginOk("T1") : Page(4, "A", "B"));
        await source.FetchPageAsync(1, 2, ct);
        await source.FetchPageAsync(2, 2, ct);
        Assert.Equal(1, handler.Logins);
        Assert.Equal(2, handler.Requests.Count(x => x.Path == DataPath));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LogsInAgainOnceWhenTheTokenIsRejected(bool viaHttpStatus)
    {
        var ct = TestContext.Current.CancellationToken;
        var (source2, handler2) = Create((seen, nth) =>
        {
            if (seen.Path == LoginPath) return LoginOk(nth == 1 ? "OLD" : "NEW");
            if (seen.Token == "OLD") return viaHttpStatus ? Json("{}", HttpStatusCode.Unauthorized) : Json("""{"code":401,"msg":"未登录"}""");
            return Page(2, "A", "B");
        });
        var page = await source2.FetchPageAsync(1, 2, ct);
        Assert.Equal(2, page.Rows.Count);
        Assert.Equal(2, handler2.Logins);
        Assert.Equal("NEW", handler2.Requests.Last().Token);
    }

    [Fact]
    public async Task FailsWhenLoginIsRejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var (source, _) = Create((_, _) => Json("""{"code":500,"msg":"账号或密码错误"}"""));
        var error = await Assert.ThrowsAsync<IpmsUnavailableException>(() => source.FetchPageAsync(1, 2, ct));
        AssertNoSecrets(error, "T1");
    }

    [Fact]
    public async Task AcceptsAPlainTokenStringAsWell()
    {
        var ct = TestContext.Current.CancellationToken;
        var (source, handler) = Create((seen, _) => seen.Path == LoginPath
            ? Json(JsonSerializer.Serialize(new { code = 200, data = new { token = "FLAT" } })) : Page(1, "A"));
        await source.FetchPageAsync(1, 2, ct);
        Assert.Equal("FLAT", handler.Requests.Last().Token);
    }

    [Fact]
    public async Task ShowsTheReasonIpmsGivesForARejectedLogin()
    {
        var ct = TestContext.Current.CancellationToken;
        var (source, _) = Create((_, _) => Json($$"""{"code":500,"type":"error","msg":"账号或密码错误 {{Password}}"}""", HttpStatusCode.InternalServerError));
        var result = await source.TestAsync(Connection(), ct);
        Assert.Equal((false, "IPMS 登录失败：账号或密码错误 ***"), (result.Ok, result.Message));
    }

    [Fact]
    public async Task PointsAtTheAddressWhenTheLoginUrlIsNotTheInterface()
    {
        var ct = TestContext.Current.CancellationToken;
        var (source, _) = Create((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("<html><body>404 Not Found</body></html>", Encoding.UTF8, "text/html"),
        });
        var result = await source.TestAsync(Connection(), ct);
        Assert.False(result.Ok);
        Assert.Contains("HTTP 404", result.Message, StringComparison.Ordinal);
        Assert.Contains("6001", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailsWhenTheTokenIsRejectedAgainAfterANewLogin()
    {
        var ct = TestContext.Current.CancellationToken;
        var (source, handler) = Create((seen, _) => seen.Path == LoginPath ? LoginOk("T1") : Json("{}", HttpStatusCode.Unauthorized));
        var error = await Assert.ThrowsAsync<IpmsUnavailableException>(() => source.FetchPageAsync(1, 2, ct));
        Assert.Equal(2, handler.Logins);
        AssertNoSecrets(error, "T1");
    }

    public static TheoryData<string> MalformedBodies => new()
    {
        "<html><body>502 Bad Gateway</body></html>",
        """{"code":500,"msg":"服务异常"}""",
        """{"code":200,"data":{"rows":[]}}""",
        """{"code":200,"data":{"total":"1"}}""",
        """{"code":200,"data":{"total":"x","rows":[]}}""",
        """{"code":200,"data":{"total":"1","rows":[{"customerName":"没有工令"}]}}""",
    };

    [Theory]
    [MemberData(nameof(MalformedBodies))]
    public async Task RejectsMalformedResponsesWithoutLeakingSecrets(string body)
    {
        var ct = TestContext.Current.CancellationToken;
        var (source, _) = Create((seen, _) => seen.Path == LoginPath ? LoginOk("T1") : Json(body));
        var error = await Assert.ThrowsAsync<IpmsUnavailableException>(() => source.FetchPageAsync(1, 2, ct));
        AssertNoSecrets(error, "T1");
    }

    [Fact]
    public async Task AbortsAPageThatExceedsTheTimeout()
    {
        var ct = TestContext.Current.CancellationToken;
        var source = new MesHttpSource(new HttpClient(new HangingHandler()), Options(), new StubSettings("http://127.0.0.1:6001"), TimeSpan.FromMilliseconds(200));
        var error = await Assert.ThrowsAsync<IpmsUnavailableException>(() => source.FetchPageAsync(1, 2, ct));
        AssertNoSecrets(error, "T1");
    }

    [Fact]
    public async Task CallerCancellationIsNotReportedAsAnOutage()
    {
        using var cts = new CancellationTokenSource();
        var source = new MesHttpSource(new HttpClient(new HangingHandler()), Options(), new StubSettings("http://127.0.0.1:6001"));
        var pending = source.FetchPageAsync(1, 2, cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task UsesTheCurrentBaseUrlAndLogsInAgainWhenItChanges()
    {
        var ct = TestContext.Current.CancellationToken;
        var (source, handler, settings) = CreateWithSettings((seen, nth) =>
            seen.Path == LoginPath ? LoginOk(seen.Host.StartsWith("10.", StringComparison.Ordinal) ? "INNER" : "OUTER") : Page(2, "A", "B"));

        await source.FetchPageAsync(1, 2, ct);
        settings.Url = "http://10.0.0.5:6001";
        await source.FetchPageAsync(2, 2, ct);

        Assert.Equal(["127.0.0.1:6001", "127.0.0.1:6001", "10.0.0.5:6001", "10.0.0.5:6001"], handler.Requests.Select(x => x.Host));
        Assert.Equal(2, handler.Logins);
        Assert.Equal("INNER", handler.Requests.Last().Token);
    }

    [Fact]
    public async Task FailsClearlyWhenNoAddressIsConfigured()
    {
        var ct = TestContext.Current.CancellationToken;
        var (source, handler, settings) = CreateWithSettings((_, _) => Page(1, "A"));
        settings.Url = null;
        var error = await Assert.ThrowsAsync<IpmsUnavailableException>(() => source.FetchPageAsync(1, 2, ct));
        Assert.Equal("IPMS 地址尚未设定", error.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task TestLogsInFreshAndFetchesOneRowWithoutTouchingTheSharedToken()
    {
        var ct = TestContext.Current.CancellationToken;
        var (source, handler, _) = CreateWithSettings((seen, nth) => seen.Path == LoginPath ? LoginOk("T" + nth) : Page(1778, "A"));
        await source.FetchPageAsync(1, 2, ct);

        var result = await source.TestAsync(Connection("http://10.0.0.9:6001"), ct);

        Assert.True(result.Ok);
        Assert.Equal(1778, result.Total);
        Assert.Contains("1778", result.Message);
        Assert.Equal(2, handler.Logins);
        var testData = handler.Requests.Last();
        Assert.Equal(("10.0.0.9:6001", "T2"), (testData.Host, testData.Token));
        var body = JsonDocument.Parse(testData.Body).RootElement.GetProperty("pageBean");
        Assert.Equal((1, 1), (body.GetProperty("page").GetInt32(), body.GetProperty("pageSize").GetInt32()));
        // The next real page still uses the original token: the test did not replace it.
        await source.FetchPageAsync(2, 2, ct);
        Assert.Equal("T1", handler.Requests.Last().Token);
        Assert.Equal(2, handler.Logins);
    }

    [Fact]
    public async Task TestLogsInWithTheGivenAccountAndPassword()
    {
        var ct = TestContext.Current.CancellationToken;
        var (source, handler, _) = CreateWithSettings((seen, _) => seen.Path == LoginPath ? LoginOk("T1") : Page(5, "A"));
        Assert.True((await source.TestAsync(new MesConnection("http://127.0.0.1:6001", "typed", "typed-pw"), ct)).Ok);
        var login = JsonDocument.Parse(handler.Requests.First(x => x.Path == LoginPath).Body).RootElement;
        Assert.Equal(("typed", "typed-pw"), (login.GetProperty("account").GetString(), login.GetProperty("password").GetString()));
    }

    [Fact]
    public async Task LogsInWithThePageSetAccountAndAgainWhenItChanges()
    {
        var ct = TestContext.Current.CancellationToken;
        var (source, handler, settings) = CreateWithSettings((seen, nth) => seen.Path == LoginPath ? LoginOk("T" + nth) : Page(2, "A", "B"));
        await source.FetchPageAsync(1, 2, ct);
        settings.Password = "changed-pw";
        await source.FetchPageAsync(2, 2, ct);

        Assert.Equal(2, handler.Logins);
        var second = JsonDocument.Parse(handler.Requests.Where(x => x.Path == LoginPath).Last().Body).RootElement;
        Assert.Equal((Account, "changed-pw"), (second.GetProperty("account").GetString(), second.GetProperty("password").GetString()));
        Assert.Equal("T2", handler.Requests.Last().Token);
    }

    [Fact]
    public async Task FailsClearlyWithoutAnAccountOrPassword()
    {
        var ct = TestContext.Current.CancellationToken;
        var (source, handler, settings) = CreateWithSettings((_, _) => Page(1, "A"));
        settings.Password = "";
        var error = await Assert.ThrowsAsync<IpmsUnavailableException>(() => source.FetchPageAsync(1, 2, ct));
        Assert.Equal("IPMS 账号或密码尚未设定", error.Message);
        Assert.Equal((false, "IPMS 账号或密码尚未设定"), ((await source.TestAsync(Connection() with { Password = "" }, ct)) is var r ? (r.Ok, r.Message) : default));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("login")]
    [InlineData("html")]
    [InlineData("network")]
    public async Task TestReportsFailuresWithoutThrowingOrLeakingSecrets(string failure)
    {
        var ct = TestContext.Current.CancellationToken;
        var (source, _) = Create((seen, _) =>
        {
            if (failure == "network") throw new HttpRequestException($"connection refused for {Account}/{Password}");
            if (seen.Path == LoginPath) return failure == "login" ? Json("""{"code":500,"msg":"bad"}""") : LoginOk("T1");
            return Json("<html>502</html>");
        });

        var result = await source.TestAsync(Connection(), ct);

        Assert.False(result.Ok);
        Assert.Null(result.Total);
        foreach (var secret in new[] { Account, Password, "T1" }) Assert.DoesNotContain(secret, result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TestWithoutAnyAddressSaysSo()
    {
        var (source, _, settings) = CreateWithSettings((_, _) => Page(1, "A"));
        settings.Url = null;
        var result = await source.TestAsync(Connection(null), TestContext.Current.CancellationToken);
        Assert.Equal((false, "IPMS 地址尚未设定"), (result.Ok, result.Message));
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        }
    }

    private static void AssertNoSecrets(Exception error, string token)
    {
        var text = error.Message + "|" + error.InnerException?.Message + "|" + error;
        foreach (var secret in new[] { Account, Password, token })
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
    }
}
