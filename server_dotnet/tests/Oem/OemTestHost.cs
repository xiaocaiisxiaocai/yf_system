using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using Yf.Api.Infrastructure;

namespace Yf.Api.Tests.Oem;

/// <summary>
/// Runs the real application (all modules, real middleware, real EF migrations) on a
/// loopback port against a disposable MySQL database, so OEM tests exercise the
/// actual HTTP contract, isolation middleware and transactions end to end.
/// </summary>
internal sealed class OemTestHost : IAsyncDisposable
{
    public const string AdminPassword = "SchemaTest#2026";
    private static readonly SemaphoreSlim ConfigEnvironmentLock = new(1, 1);

    private readonly SchemaShapeTests.SchemaDatabaseScope scope;
    private readonly WebApplication app;
    private readonly List<HttpClient> clients = [];

    private OemTestHost(SchemaShapeTests.SchemaDatabaseScope scope, WebApplication app, Uri baseAddress, string storageRoot, string connectionString)
    {
        this.scope = scope;
        this.app = app;
        BaseAddress = baseAddress;
        StorageRoot = storageRoot;
        ConnectionString = connectionString;
    }

    public Uri BaseAddress { get; }
    public string StorageRoot { get; }
    public string ConnectionString { get; }
    public IServiceProvider Services => app.Services;

    public static async Task<OemTestHost> StartAsync(CancellationToken ct, Action<List<string>>? extraArgs = null)
    {
        var scope = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("oem", ct);
        string? hostConnectionString = null;
        WebApplication? app = null;
        try
        {
            await scope.InitializeAsync(ct);
            var connectionString = new MySqlConnectionStringBuilder(scope.Options.ConnectionString)
            {
                MaximumPoolSize = 20,
            }.ConnectionString;
            hostConnectionString = connectionString;
            await using (var conn = new MySqlConnection(connectionString))
            {
                await conn.OpenAsync(ct);
                await conn.ExecuteAsync("UPDATE users SET must_change_password=0, email='admin@example.invalid' WHERE employee_no='admin'");
            }
            var storageRoot = Path.Combine(Path.GetTempPath(), "yf_oem_tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(storageRoot);
            var args = new List<string>
            {
                "--environment=Development",
                "--App:ConnectionString=" + connectionString,
                "--App:JwtSecret=oem-test-secret-0123456789-abcdefghijklmnop",
                "--App:StorageRoot=" + Path.Combine(storageRoot, "collab"),
                "--App:WorkerEnabled=false",
                "--App:CookieSecure=false",
                "--App:WebBaseUrl=http://127.0.0.1:5273",
                "--App:OemStorageRoot=" + Path.Combine(storageRoot, "oem"),
            };
            extraArgs?.Invoke(args);
            await ConfigEnvironmentLock.WaitAsync(ct);
            var previousConfig = Environment.GetEnvironmentVariable("YF_CONFIG_PATH");
            try
            {
                Environment.SetEnvironmentVariable("YF_CONFIG_PATH", null);
                app = await ApiApplication.BuildAsync([.. args], builder => builder.WebHost.UseUrls("http://127.0.0.1:0"));
            }
            finally
            {
                Environment.SetEnvironmentVariable("YF_CONFIG_PATH", previousConfig);
                ConfigEnvironmentLock.Release();
            }
            await app!.StartAsync(ct);
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            return new OemTestHost(scope, app, new Uri(address), storageRoot, connectionString);
        }
        catch
        {
            if (app is not null)
            {
                try { await app.StopAsync(); } catch { }
                await app.DisposeAsync();
            }
            if (hostConnectionString is not null)
                TestDatabasePoolCleanup.Clear(hostConnectionString,
                    AppDb.BuildConnectionString(new AppOptions { ConnectionString = hostConnectionString }));
            await scope.DisposeAsync();
            throw;
        }
    }

    public ApiClient Anonymous()
    {
        var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), UseCookies = true };
        var http = new HttpClient(handler) { BaseAddress = BaseAddress };
        http.DefaultRequestHeaders.Add("Origin", "http://127.0.0.1:5273");
        clients.Add(http);
        return new ApiClient(http, handler.CookieContainer);
    }

    public async Task<ApiClient> LoginInternalAsync(string employeeNo, string password, CancellationToken ct)
    {
        var client = Anonymous();
        var body = await client.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new { employeeNo, password }, ct).Ok();
        client.Bearer(body["accessToken"]!.GetValue<string>());
        return client;
    }

    public Task<ApiClient> LoginAdminAsync(CancellationToken ct) => LoginInternalAsync("admin", AdminPassword, ct);

    public async Task<ApiClient> LoginOemAsync(string employeeNo, string password, CancellationToken ct)
    {
        var client = Anonymous();
        var body = await client.SendAsync(HttpMethod.Post, "/api/v1/oem/auth/login", new { employeeNo, password }, ct).Ok();
        client.Bearer(body["accessToken"]!.GetValue<string>());
        return client;
    }

    public T Service<T>() where T : notnull => app.Services.GetRequiredService<T>();

    /// <summary>Runs validation and promotion until no work is left (workers are disabled in tests).</summary>
    public async Task RunOemJobsAsync(CancellationToken ct)
    {
        for (var round = 0; round < 20; round++)
        {
            var validated = await Service<Yf.Api.Modules.Oem.Validation.OemFileValidationService>().RunOnceAsync(ct);
            var promoted = await Service<Yf.Api.Modules.Oem.Validation.OemPromotionService>().RunOnceAsync(ct);
            if (validated == 0 && promoted == 0) return;
        }
        throw new InvalidOperationException("OEM jobs did not settle");
    }

    /// <summary>Uploads <paramref name="content"/> through init → chunks → merge and returns the merged file JSON.</summary>
    public async Task<JsonNode> UploadAsync(ApiClient client, ulong transferId, string fileName, byte[] content, CancellationToken ct)
    {
        if (!clients.Contains(client.Http)) throw new ArgumentException("The API client does not belong to this OEM test host.", nameof(client));
        var init = await client.PostAsync($"/api/v1/oem/transfers/{transferId}/uploads/init", new { fileName, fileSize = (ulong)content.Length }, ct).Ok();
        var sessionId = init["sessionId"]!.GetValue<string>();
        var chunkSize = init["chunkSize"]!.GetValue<int>();
        var total = init["totalChunks"]!.GetValue<int>();
        for (var index = 0; index < total; index++)
        {
            var slice = content.AsMemory(index * chunkSize, Math.Min(chunkSize, content.Length - index * chunkSize)).ToArray();
            using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/oem/uploads/{sessionId}/chunks/{index}") { Content = new ByteArrayContent(slice) };
            using var response = await client.Http.SendAsync(request, ct);
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(ct));
        }
        return await client.PostAsync($"/api/v1/oem/uploads/{sessionId}/merge", null, ct).Ok();
    }

    public async Task<ApiClient> CreateVendorAsync(ApiClient admin, string companyName, string employeeNo, CancellationToken ct)
    {
        var companyId = (await admin.PostAsync("/api/v1/oem/companies", new { name = companyName }, ct).Ok()).Id();
        await admin.PostAsync($"/api/v1/oem/companies/{companyId}/accounts",
            new { employeeNo, realName = "厂商" + employeeNo, email = employeeNo + "@vendor.invalid", password = "Vendor#2026" }, ct).Ok();
        await using (var conn = await OpenAsync(ct))
            await conn.ExecuteAsync("UPDATE oem_accounts SET must_change_password=0 WHERE employee_no=@employeeNo", new { employeeNo });
        return await LoginOemAsync(employeeNo, "Vendor#2026", ct);
    }

    public async Task<ulong> CompanyIdOfAsync(string employeeNo, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        return await conn.ExecuteScalarAsync<ulong>("SELECT oem_company_id FROM oem_accounts WHERE employee_no=@employeeNo", new { employeeNo });
    }

    public static byte[] Pdf(string text) => System.Text.Encoding.ASCII.GetBytes("%PDF-1.4\n" + text + "\n%%EOF\n");

    public async Task<MySqlConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new MySqlConnection(ConnectionString);
        await conn.OpenAsync(ct);
        return conn;
    }

    /// <summary>Creates an internal user with a fresh role holding exactly <paramref name="permissionCodes"/>, optionally inside a department.</summary>
    public async Task<ulong> CreateInternalUserAsync(string employeeNo, string password, string[] permissionCodes, ulong? departmentId, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        var hash = await Yf.Api.Modules.Identity.PasswordService.HashAsync(password, ct);
        var userId = await conn.ExecuteScalarAsync<ulong>(
            "INSERT INTO users(employee_no,password_hash,real_name,email,user_type,department_id,status,must_change_password,failed_login_attempts) " +
            "VALUES(@employeeNo,@hash,@name,@email,'INTERNAL',@departmentId,'ACTIVE',0,0); SELECT LAST_INSERT_ID();",
            new { employeeNo, hash, name = "测试" + employeeNo, email = employeeNo + "@example.invalid", departmentId });
        var roleId = await conn.ExecuteScalarAsync<ulong>(
            "INSERT INTO roles(name,is_built_in,status) VALUES(@name,0,'ACTIVE'); SELECT LAST_INSERT_ID();",
            new { name = "角色_" + employeeNo });
        foreach (var code in permissionCodes)
            await conn.ExecuteAsync("INSERT INTO role_permissions(role_id,permission_id) SELECT @roleId,id FROM permissions WHERE code=@code",
                new { roleId, code });
        await conn.ExecuteAsync("INSERT INTO user_roles(user_id,role_id) VALUES(@userId,@roleId)", new { userId, roleId });
        return userId;
    }

    public async Task<ulong> CreateSupplierUserAsync(string employeeNo, string password, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        var supplierId = await conn.ExecuteScalarAsync<ulong>(
            "INSERT INTO suppliers(name,status) VALUES(@name,'ACTIVE'); SELECT LAST_INSERT_ID();", new { name = "供应商_" + employeeNo });
        var hash = await Yf.Api.Modules.Identity.PasswordService.HashAsync(password, ct);
        return await conn.ExecuteScalarAsync<ulong>(
            "INSERT INTO users(employee_no,password_hash,real_name,email,user_type,supplier_id,status,must_change_password,failed_login_attempts) " +
            "VALUES(@employeeNo,@hash,'供应商人员',@email,'SUPPLIER',@supplierId,'ACTIVE',0,0); SELECT LAST_INSERT_ID();",
            new { employeeNo, hash, email = employeeNo + "@example.invalid", supplierId });
    }

    /// <summary>Creates DIVISION &gt; DEPARTMENT &gt; SECTION and returns their ids.</summary>
    public async Task<(ulong Division, ulong Department, ulong Section)> CreateOrgPathAsync(string prefix, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        var division = await conn.ExecuteScalarAsync<ulong>(
            "INSERT INTO departments(name,parent_id,kind,status) VALUES(@name,NULL,'DIVISION','ACTIVE'); SELECT LAST_INSERT_ID();", new { name = prefix + "事业部" });
        var department = await conn.ExecuteScalarAsync<ulong>(
            "INSERT INTO departments(name,parent_id,kind,status) VALUES(@name,@division,'DEPARTMENT','ACTIVE'); SELECT LAST_INSERT_ID();", new { name = prefix + "部门", division });
        var section = await conn.ExecuteScalarAsync<ulong>(
            "INSERT INTO departments(name,parent_id,kind,status) VALUES(@name,@department,'SECTION','ACTIVE'); SELECT LAST_INSERT_ID();", new { name = prefix + "课", department });
        return (division, department, section);
    }

    public async ValueTask DisposeAsync()
    {
        // The HTTP host uses a larger, separately keyed pool than the schema fixture.
        // Reclaim both pools before dropping its temporary database; otherwise repeated
        // OEM hosts retain idle connections until the disposable server reaches its cap.
        var applicationConnectionString = AppDb.BuildConnectionString(Service<AppOptions>());
        foreach (var client in clients) client.Dispose();
        try { await app.StopAsync(); } catch { }
        await app.DisposeAsync();
        TestDatabasePoolCleanup.Clear(ConnectionString, applicationConnectionString);
        await scope.DisposeAsync();
        try { Directory.Delete(StorageRoot, recursive: true); } catch { }
    }
}

internal sealed class ApiClient(HttpClient http, CookieContainer cookies)
{
    public HttpClient Http { get; } = http;
    public CookieContainer Cookies { get; } = cookies;

    public void Bearer(string token) => Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    public async Task<ApiResponse> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await Http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        return new ApiResponse(response.StatusCode, text);
    }

    public Task<ApiResponse> GetAsync(string path, CancellationToken ct) => SendAsync(HttpMethod.Get, path, null, ct);
    public Task<ApiResponse> PostAsync(string path, object? body, CancellationToken ct) => SendAsync(HttpMethod.Post, path, body ?? new { }, ct);
    public Task<ApiResponse> PutAsync(string path, object? body, CancellationToken ct) => SendAsync(HttpMethod.Put, path, body ?? new { }, ct);
    public Task<ApiResponse> DeleteAsync(string path, CancellationToken ct) => SendAsync(HttpMethod.Delete, path, null, ct);
}

internal sealed record ApiResponse(HttpStatusCode Status, string Body)
{
    public JsonNode? Json => string.IsNullOrWhiteSpace(Body) ? null : JsonNode.Parse(Body);
    public int? Code => Json is JsonObject obj && obj["code"] is JsonValue value ? value.GetValue<int>() : null;

    public JsonNode Expect(HttpStatusCode status)
    {
        Assert.True(Status == status, $"Expected {(int)status} but got {(int)Status}: {Body}");
        return Json ?? new JsonObject();
    }
}

internal static class ApiResponseExtensions
{
    public static async Task<JsonNode> Ok(this Task<ApiResponse> response) => (await response).Expect(HttpStatusCode.OK);

    public static async Task<ApiResponse> Status(this Task<ApiResponse> response, HttpStatusCode status, int? code = null)
    {
        var result = await response;
        Assert.True(result.Status == status, $"Expected {(int)status} but got {(int)result.Status}: {result.Body}");
        if (code is not null) Assert.Equal(code, result.Code);
        return result;
    }

    public static ulong Id(this JsonNode node, string property = "id") => node[property]!.GetValue<ulong>();
}
