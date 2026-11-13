using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Yf.Api.Modules.Outsource.Ipms.Mes;

/// <summary>
/// Reads the 迅得二期 IPMS MES monitoring list over HTTP: logs in with the configured account, keeps the token in memory,
/// and logs in again once when MES rejects it. Messages and exceptions never carry the account, password or token.
/// The login response shape (<c>data.token</c>) and the "not logged in" signals (HTTP 401/403 or JSON code 401/403)
/// are assumptions to confirm against the real system; see the plan's Task 6.
/// </summary>
public sealed class MesHttpSource(HttpClient http, IpmsOptions options, IMesSettings settings, TimeSpan? pageTimeoutOverride = null) : IMesSource
{
    private const string LoginPath = "/xyj-admin/r/login";
    private const string DataPath = "/xyj-twxd-mes/r/xdSalesOrder/queryByMesMonitor";
    private const string Unavailable = "IPMS 暂时无法连接，请稍后再试";
    private const string NoAddress = "IPMS 地址尚未设定";
    private const string NoAccount = "IPMS 账号或密码尚未设定";

    private readonly SemaphoreSlim gate = new(1, 1);
    private string? token;
    /// <summary>The connection <see cref="token"/> belongs to; another address, account or password means another session.</summary>
    private MesConnection? session;

    public async Task<MesPage> FetchPageAsync(int page, int pageSize, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(pageTimeoutOverride ?? TimeSpan.FromSeconds(options.MesPageTimeoutSeconds));
            try
            {
                var connection = await settings.ConnectionAsync(timeout.Token);
                if (string.IsNullOrWhiteSpace(connection.BaseUrl)) throw new IpmsUnavailableException(NoAddress);
                if (!connection.Complete) throw new IpmsUnavailableException(NoAccount);
                if (connection != session) { token = null; session = connection; }
                var baseUrl = connection.BaseUrl!;
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    token ??= await LoginAsync(connection, timeout.Token);
                    var (status, body) = await PostAsync(baseUrl, DataPath, DataBody(page, pageSize), token, timeout.Token);
                    if (!IsAuthFailure(status, body)) return Parse(status, body);
                    token = null; // expired or kicked: log in again once
                }
                throw new IpmsUnavailableException(Unavailable);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new IpmsUnavailableException(Unavailable); // the per-page timeout, not the caller
            }
            catch (HttpRequestException)
            {
                throw new IpmsUnavailableException(Unavailable);
            }
        }
        finally { gate.Release(); }
    }

    public async Task<MesTestResult> TestAsync(MesConnection connection, CancellationToken ct)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        MesTestResult Failed(string message) => new(false, message, null, (int)started.ElapsedMilliseconds);
        if (string.IsNullOrWhiteSpace(connection.BaseUrl)) return Failed(NoAddress);
        if (!connection.Complete) return Failed(NoAccount);
        var url = connection.BaseUrl.Trim();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(pageTimeoutOverride ?? TimeSpan.FromSeconds(options.MesPageTimeoutSeconds));
        try
        {
            var testToken = await LoginAsync(connection with { BaseUrl = url }, timeout.Token);
            var (status, body) = await PostAsync(url, DataPath, DataBody(1, 1), testToken, timeout.Token);
            if (IsAuthFailure(status, body)) return Failed("IPMS 登录后仍被拒绝，请检查账号的 MES 模块权限");
            var page = Parse(status, body);
            return new(true, $"连接成功，IPMS MES 监控共 {page.Total} 笔", page.Total, (int)started.ElapsedMilliseconds);
        }
        catch (IpmsUnavailableException unavailable) { return Failed(unavailable.Message); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return Failed(Unavailable); }
        catch (HttpRequestException) { return Failed(Unavailable); }
    }

    private async Task<string> LoginAsync(MesConnection connection, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["account"] = connection.Account, ["password"] = connection.Password, ["captcha"] = "",
            ["usertype"] = 0, ["yzmState"] = 0, ["suffix"] = "",
        };
        var (status, text) = await PostAsync(connection.BaseUrl!, LoginPath, body, null, ct);
        if (status == HttpStatusCode.OK && TryParse(text, out var root) && Code(root) == 200)
        {
            var value = root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object && data.TryGetProperty("token", out var t) ? t
                : root.TryGetProperty("token", out t) ? t : default;
            if (value.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(value.GetString())) return value.GetString()!;
        }
        throw new IpmsUnavailableException("IPMS 登录失败，请检查账号和密码");
    }

    private async Task<(HttpStatusCode Status, string Body)> PostAsync(string baseUrl, string path, JsonNode body, string? tokenValue, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl.TrimEnd('/') + path) { Content = JsonContent.Create(body) };
        if (tokenValue is not null) request.Headers.Add("token", tokenValue);
        using var response = await http.SendAsync(request, ct);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(ct));
    }

    private static JsonObject DataBody(int page, int pageSize) => new()
    {
        ["pageBean"] = new JsonObject { ["page"] = page, ["pageSize"] = pageSize, ["showTotal"] = true, ["summary"] = new JsonArray() },
        ["queryComplaint"] = new JsonObject(),
        ["params"] = new JsonObject(),
        ["querys"] = new JsonArray
        {
            Query("productionStatus", "1", "2", "0"),
            Query("outMachineStatus", "0"),
        },
        ["selfDate"] = new JsonObject(),
        ["sorter"] = new JsonArray(),
        ["countFlag"] = false,
        ["dataAuthority"] = false,
        ["permissionsGroup"] = false,
    };

    private static JsonObject Query(string property, params string[] values) => new()
    {
        ["hasInitValue"] = false, ["operation"] = "IN", ["property"] = property, ["relation"] = "AND",
        ["value"] = new JsonArray(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()),
    };

    private static bool IsAuthFailure(HttpStatusCode status, string body) =>
        status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
        || (TryParse(body, out var root) && Code(root) is 401 or 403);

    private static MesPage Parse(HttpStatusCode status, string body)
    {
        if (status != HttpStatusCode.OK || !TryParse(body, out var root) || Code(root) != 200
            || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array
            || !data.TryGetProperty("total", out var totalValue) || !TryInt(totalValue, out var total))
            throw new IpmsUnavailableException("IPMS 返回了无法识别的数据");
        var result = new List<MesRow>(rows.GetArrayLength());
        foreach (var row in rows.EnumerateArray())
        {
            var no = row.ValueKind == JsonValueKind.Object && row.TryGetProperty("workOrder", out var w)
                ? w.ValueKind switch { JsonValueKind.String => w.GetString(), JsonValueKind.Number => w.GetRawText(), _ => null }
                : null;
            if (string.IsNullOrWhiteSpace(no)) throw new IpmsUnavailableException("IPMS 返回了无法识别的数据");
            result.Add(new(no.Trim(), row.GetRawText()));
        }
        return new(total, result);
    }

    private static bool TryParse(string text, out JsonElement root)
    {
        try
        {
            root = JsonDocument.Parse(text).RootElement.Clone();
            return root.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            root = default;
            return false;
        }
    }

    private static int Code(JsonElement root) =>
        root.TryGetProperty("code", out var code) && TryInt(code, out var value) ? value : -1;

    private static bool TryInt(JsonElement value, out int result)
    {
        result = 0;
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetInt32(out result),
            JsonValueKind.String => int.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out result),
            _ => false,
        };
    }
}
