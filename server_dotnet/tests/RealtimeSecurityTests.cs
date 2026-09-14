using System.Diagnostics;
using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Identity;
using Yf.Api.Modules.Projects;

namespace Yf.Api.Tests;

[Collection(ConnectionLifecycleCollection.Name)]
public sealed class RealtimeSecurityTests
{
    [Fact]
    public void QueryTokenIsAcceptedOnlyOnTheExactHubPath()
    {
        var hub = Request(ProjectRealtimeHub.Path, "access_token=hub-token");
        Assert.True(IdentityMiddleware.TryGetAccessToken(hub.Request, out var token));
        Assert.Equal("hub-token", token);

        foreach (var path in new[]
                 {
                     "/api/v1/projects",
                     ProjectRealtimeHub.Path + "/negotiate",
                     ProjectRealtimeHub.Path + "/extra",
                 })
        {
            var request = Request(path, "access_token=query-token");
            Assert.False(IdentityMiddleware.TryGetAccessToken(request.Request, out _));
        }

        var unauthenticated = Request(ProjectRealtimeHub.Path, null);
        Assert.False(IdentityMiddleware.TryGetAccessToken(unauthenticated.Request, out _));

        var ordinaryApi = Request("/api/v1/projects", "access_token=query-token");
        ordinaryApi.Request.Headers.Authorization = "Bearer header-token";
        Assert.True(IdentityMiddleware.TryGetAccessToken(ordinaryApi.Request, out token));
        Assert.Equal("header-token", token);
    }

    [Fact]
    public void ExpiredConnectionsAreNotRegisteredAndActiveConnectionsCanBeAborted()
    {
        using var registry = new RealtimeConnectionRegistry();
        var aborts = 0;
        Assert.False(registry.Register(
            new RealtimeConnection("expired", 1, "session", DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 1),
            () => aborts++));
        Assert.Equal(1, aborts);
        Assert.Empty(registry.Snapshot());

        Assert.True(registry.Register(
            new RealtimeConnection("active", 1, "session", DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeSeconds()),
            () => aborts++));
        Assert.Single(registry.Snapshot());
        registry.Disconnect("active");
        Assert.Equal(2, aborts);
        Assert.Empty(registry.Snapshot());
    }

    [Fact(Timeout = 60_000)]
    public async Task DeliveryRechecksPermissionAndSessionAndPushFailureDoesNotUndoCommittedMessage()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await LocalDatabaseScope.CreateOrSkipAsync("realtime", ct);
        await database.InitializeAsync(ct);
        await database.SeedAsync("""
            UPDATE users SET must_change_password=0 WHERE id=1;
            INSERT INTO suppliers(id,name,status,created_by) VALUES(100,'实时供应商','ACTIVE',1);
            INSERT INTO roles(id,name,description,is_built_in,status)
            VALUES(9001,'实时项目成员','实时权限复核',0,'ACTIVE');
            INSERT INTO role_permissions(role_id,permission_id)
            SELECT 9001,id FROM permissions WHERE code='project:list';
            INSERT INTO users
                (id,employee_no,password_hash,real_name,email,user_type,supplier_id,status,must_change_password)
            VALUES(101,'realtime-member','unused','实时成员','','INTERNAL',NULL,'ACTIVE',0);
            INSERT INTO user_roles(user_id,role_id) VALUES(101,9001);
            INSERT INTO projects(id,name,supplier_id,status,created_by)
            VALUES(1001,'实时项目',100,'IN_PROGRESS',1);
            INSERT INTO project_members(project_id,user_id,created_by) VALUES(1001,101,1);
            INSERT INTO refresh_tokens(user_id,session_id,token_hash,expires_at,revoked,ip)
            VALUES(101,'realtime-session',REPEAT('a',64),DATE_ADD(UTC_TIMESTAMP(),INTERVAL 1 HOUR),0,NULL);
            INSERT INTO messages(id,project_id,sender_id,content,status)
            VALUES(10001,1001,101,'待删除留言','NORMAL');
            """, ct);

        var authorizer = new ProjectRealtimeAuthorizer(database.Database);
        var connection = new RealtimeConnection(
            "connection", 101, "realtime-session", DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds());
        Assert.True(await authorizer.ValidateIdentityAsync(connection, ct));
        Assert.Equal(ProjectRealtimeAuthorization.Deliver,
            await authorizer.AuthorizeProjectAsync(connection, 1001, ct));

        await database.SeedAsync("""
            DELETE rp FROM role_permissions rp
            INNER JOIN permissions p ON p.id=rp.permission_id
            WHERE rp.role_id=9001 AND p.code='project:list';
            """, ct);
        Assert.Equal(ProjectRealtimeAuthorization.Skip,
            await authorizer.AuthorizeProjectAsync(connection, 1001, ct));

        await database.SeedAsync("""
            INSERT INTO role_permissions(role_id,permission_id)
            SELECT 9001,id FROM permissions WHERE code='project:list';
            UPDATE refresh_tokens SET expires_at=DATE_SUB(UTC_TIMESTAMP(),INTERVAL 1 SECOND)
            WHERE session_id='realtime-session';
            """, ct);
        Assert.Equal(ProjectRealtimeAuthorization.Disconnect,
            await authorizer.AuthorizeProjectAsync(connection, 1001, ct));

        var failingPublisher = new ThrowingPublisher();
        var messages = new MessageService(new AuditService([]), database.Options, failingPublisher);
        await using (var db = await database.Database.OpenAsync(ct))
        {
            await messages.DeleteAsync(db, new CurrentUser(1, "admin", "INTERNAL", null), 10001, null, ct);
            Assert.Equal("DELETED", await db.ExecuteScalarAsync<string>(new CommandDefinition(
                "SELECT status FROM messages WHERE id=10001", cancellationToken: ct)));
        }
        var call = Assert.Single(failingPublisher.Calls);
        Assert.Equal(1001UL, call.ProjectId);
        Assert.Equal(RealtimeChangeKinds.Messages, call.Kind);

        await database.SeedAsync("""
            UPDATE refresh_tokens SET expires_at=DATE_ADD(UTC_TIMESTAMP(),INTERVAL 1 HOUR),revoked=0
            WHERE session_id='realtime-session';
            INSERT INTO messages(id,project_id,sender_id,content,status)
            VALUES(10002,1001,101,'连接池派发留言','NORMAL');
            """, ct);
        using var registry = new RealtimeConnectionRegistry();
        Assert.True(registry.Register(
            connection with { ConnectionId = "delivery" },
            () => { }));
        var hub = new RecordingHubContext();
        var queuedPublisher = new ProjectRealtimePublisher(
            hub, registry, authorizer, NullLogger<ProjectRealtimePublisher>.Instance);
        await queuedPublisher.StartAsync(ct);
        try
        {
            var queuedMessages = new MessageService(new AuditService([]), database.Options, queuedPublisher);
            var elapsed = Stopwatch.StartNew();
            await using (var routeConnection = await database.Database.OpenAsync(ct))
                await queuedMessages.DeleteAsync(routeConnection,
                    new CurrentUser(1, "admin", "INTERNAL", null), 10002, null, ct);
            elapsed.Stop();
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(4),
                $"Committed message request waited {elapsed.Elapsed} for realtime dispatch");

            var delivery = await hub.Proxy.Delivery.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            Assert.Equal(ProjectRealtimeHub.ProjectChangedEvent, delivery.Method);
            var payload = Assert.IsType<ProjectChangedPayload>(Assert.Single(delivery.Args));
            Assert.Equal(1001UL, payload.ProjectId);
            Assert.Equal(RealtimeChangeKinds.Messages, payload.Kind);
        }
        finally
        {
            await queuedPublisher.StopAsync(CancellationToken.None);
            queuedPublisher.Dispose();
        }
    }

    private static DefaultHttpContext Request(string path, string? query)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        if (query is not null) context.Request.QueryString = new QueryString("?" + query);
        return context;
    }

    private sealed class ThrowingPublisher : IProjectRealtimePublisher
    {
        internal List<(ulong ProjectId, string Kind)> Calls { get; } = [];

        public Task PublishAsync(ulong projectId, string kind, CancellationToken ct = default)
        {
            Calls.Add((projectId, kind));
            throw new InvalidOperationException("simulated realtime failure");
        }
    }

    private sealed class LocalDatabaseScope(
        MySqlConnection administration,
        string databaseName,
        AppDb database,
        AppOptions options) : IAsyncDisposable
    {
        internal AppDb Database { get; } = database;
        internal AppOptions Options { get; } = options;

        internal static async Task<LocalDatabaseScope> CreateOrSkipAsync(string purpose, CancellationToken ct)
        {
            var raw = Environment.GetEnvironmentVariable("YF_TEST_DATABASE_URL");
            if (string.IsNullOrWhiteSpace(raw)) Assert.Skip("YF_TEST_DATABASE_URL is not set");
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.Scheme != "mysql")
                throw new InvalidOperationException("YF_TEST_DATABASE_URL must be a mysql:// URL");
            if (uri.Host is not ("127.0.0.1" or "localhost" or "::1"))
                throw new InvalidOperationException("Realtime tests only allow local MySQL");
            var credentials = uri.UserInfo.Split(':', 2);
            var adminOptions = new MySqlConnectionStringBuilder
            {
                Server = uri.Host,
                Port = (uint)(uri.IsDefaultPort ? 3306 : uri.Port),
                UserID = Uri.UnescapeDataString(credentials[0]),
                Password = credentials.Length == 2 ? Uri.UnescapeDataString(credentials[1]) : string.Empty,
                DateTimeKind = MySqlDateTimeKind.Utc,
                SslMode = MySqlSslMode.None,
            };
            var administration = new MySqlConnection(adminOptions.ConnectionString);
            await administration.OpenAsync(ct);
            var databaseName = $"yf_test_dotnet_{purpose}_{Guid.NewGuid():N}";
            try
            {
                await administration.ExecuteAsync(new CommandDefinition(
                    $"CREATE DATABASE `{databaseName}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci",
                    cancellationToken: ct));
                var options = new AppOptions
                {
                    ConnectionString = new MySqlConnectionStringBuilder(adminOptions.ConnectionString)
                    {
                        Database = databaseName,
                        MaximumPoolSize = 1,
                        MinimumPoolSize = 0,
                    }.ConnectionString,
                    StorageRoot = Path.Combine(Path.GetTempPath(), "yf_realtime_storage"),
                    WorkerEnabled = false,
                };
                return new(administration, databaseName, new AppDb(options), options);
            }
            catch
            {
                try { await administration.ExecuteAsync($"DROP DATABASE IF EXISTS `{databaseName}`"); }
                finally { await administration.DisposeAsync(); }
                throw;
            }
        }

        internal async Task InitializeAsync(CancellationToken ct)
        {
            var previousPassword = Environment.GetEnvironmentVariable("YF_BOOTSTRAP_PASSWORD");
            Environment.SetEnvironmentVariable("YF_BOOTSTRAP_PASSWORD", "Realtime#" + Guid.NewGuid().ToString("N")[..8] + "!");
            try { await SchemaBootstrap.InitializeEmptyAsync(Database, ct); }
            finally { Environment.SetEnvironmentVariable("YF_BOOTSTRAP_PASSWORD", previousPassword); }
        }

        internal async Task SeedAsync(string sql, CancellationToken ct)
        {
            await using var connection = await Database.OpenAsync(ct);
            await connection.ExecuteAsync(new CommandDefinition(sql, cancellationToken: ct));
        }

        public async ValueTask DisposeAsync()
        {
            try { await administration.ExecuteAsync($"DROP DATABASE IF EXISTS `{databaseName}`"); }
            finally { await administration.DisposeAsync(); }
        }
    }

    private sealed class RecordingHubContext : IHubContext<ProjectRealtimeHub>
    {
        internal RecordingClientProxy Proxy { get; } = new();
        public IHubClients Clients { get; }
        public IGroupManager Groups { get; } = new NullGroupManager();

        internal RecordingHubContext()
        {
            Clients = new RecordingHubClients(Proxy);
        }
    }

    private sealed class RecordingHubClients(IClientProxy proxy) : IHubClients
    {
        public IClientProxy All => proxy;
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => proxy;
        public IClientProxy Client(string connectionId) => proxy;
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => proxy;
        public IClientProxy Group(string groupName) => proxy;
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => proxy;
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => proxy;
        public IClientProxy User(string userId) => proxy;
        public IClientProxy Users(IReadOnlyList<string> userIds) => proxy;
    }

    private sealed class RecordingClientProxy : IClientProxy
    {
        internal TaskCompletionSource<(string Method, object?[] Args)> Delivery { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            Delivery.TrySetResult((method, args));
            return Task.CompletedTask;
        }
    }

    private sealed class NullGroupManager : IGroupManager
    {
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
