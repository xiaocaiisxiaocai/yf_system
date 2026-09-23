using System.Text;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Projects;

namespace Yf.Api.Tests;

[Collection(ConnectionLifecycleCollection.Name)]
public sealed class MessageBodyConnectionTests
{
    [Theory(Timeout = 120_000)]
    [InlineData("permission", 403)]
    [InlineData("project-state", 409)]
    public async Task SlowMultipartBodyReleasesPoolAndCreateRechecksChangedState(
        string change,
        int expectedStatus)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await MigratedTestDatabase.CreateOrSkipAsync(ct);
        await database.ExecuteAsync("""
            UPDATE users SET must_change_password=0 WHERE id=1;
            INSERT INTO suppliers(id,name,status,created_by)
                VALUES(9700,'slow body supplier','ACTIVE',1);
            INSERT INTO project_groups(id,name,supplier_id,status,created_by,responsible_user_id)
                VALUES(9710,'slow body group',9700,'IN_PROGRESS',1,1);
            INSERT INTO projects(id,project_group_id,name,supplier_id,status,created_by,responsible_user_id)
                VALUES(9720,9710,'slow body project',9700,'IN_PROGRESS',1,1);
            """, null, ct);

        var options = SingleConnectionOptions(database.Options);
        var pooledDatabase = new AppDb(options);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(pooledDatabase);
        builder.Services.AddSingleton<AccessService>();
        builder.Services.AddSingleton<AuditService>();
        builder.Services.AddProjectsModule();
        await using var application = builder.Build();
        application.MapProjectsModule();
        var endpoint = ((IEndpointRouteBuilder)application).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(item => item.RoutePattern.RawText == "/api/v1/projects/{id:long}/messages"
                && item.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains(HttpMethods.Post) == true);
        Assert.NotNull(endpoint.RequestDelegate);

        const string boundary = "YfSlowMessageBoundary";
        var multipart = Encoding.UTF8.GetBytes(
            $"--{boundary}\r\nContent-Disposition: form-data; name=\"content\"\r\n\r\nslow request\r\n--{boundary}--\r\n");
        await using var body = new GatedReadStream(multipart);
        await using var requestScope = application.Services.CreateAsyncScope();
        var context = new DefaultHttpContext
        {
            RequestServices = requestScope.ServiceProvider,
            Response = { Body = new MemoryStream() },
        };
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/api/v1/projects/9720/messages";
        context.Request.RouteValues["id"] = "9720";
        context.Request.ContentType = $"multipart/form-data; boundary={boundary}";
        context.Request.ContentLength = multipart.Length;
        context.Request.Body = body;
        context.Items[typeof(CurrentUser)] = new CurrentUser(1, "admin", "INTERNAL", null);
        using var requestDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        requestDeadline.CancelAfter(TimeSpan.FromSeconds(20));
        context.RequestAborted = requestDeadline.Token;

        var request = endpoint.RequestDelegate!(context);
        try
        {
            await body.ReadStarted.WaitAsync(TimeSpan.FromSeconds(5), ct);

            // The only application pool slot must be available while multipart parsing is paused.
            using var poolDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            poolDeadline.CancelAfter(TimeSpan.FromSeconds(5));
            await using (var probe = await pooledDatabase.OpenAsync(poolDeadline.Token))
            {
                Assert.Equal(1, await probe.ExecuteScalarAsync<int>(new CommandDefinition(
                    "SELECT 1", cancellationToken: poolDeadline.Token)));
                var affected = change switch
                {
                    "permission" => await probe.ExecuteAsync(new CommandDefinition("""
                        DELETE FROM role_permissions
                        WHERE role_id IN (SELECT role_id FROM user_roles WHERE user_id=1)
                          AND permission_id=(SELECT id FROM permissions WHERE code='message:create')
                        """, cancellationToken: poolDeadline.Token)),
                    "project-state" => await probe.ExecuteAsync(new CommandDefinition(
                        "UPDATE projects SET status='COMPLETED' WHERE id=9720 AND status='IN_PROGRESS'",
                        cancellationToken: poolDeadline.Token)),
                    _ => throw new ArgumentOutOfRangeException(nameof(change)),
                };
                Assert.True(affected > 0, $"Race mutation '{change}' did not change the fixture.");
            }

            body.Release();
            var error = await Assert.ThrowsAsync<ApiException>(async () => await request);
            Assert.Equal(expectedStatus, error.Status);
        }
        finally
        {
            body.Release();
            try { await request; }
            catch { }
        }

        Assert.Equal(0, await database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM messages WHERE project_id=9720", ct));
        Assert.Equal(0, await database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM audit_logs WHERE action='MESSAGE_CREATE' AND target_type='message'", ct));
    }

    private static AppOptions SingleConnectionOptions(AppOptions source) => new()
    {
        ConnectionString = new MySqlConnectionStringBuilder(source.ConnectionString)
        {
            MaximumPoolSize = 1,
            MinimumPoolSize = 0,
        }.ConnectionString,
        StorageRoot = source.StorageRoot,
        WebBaseUrl = source.WebBaseUrl,
        UploadMaxFileSize = source.UploadMaxFileSize,
        WorkerEnabled = false,
    };

    private sealed class GatedReadStream(byte[] content) : Stream
    {
        private readonly MemoryStream inner = new(content, writable: false);
        private readonly TaskCompletionSource readStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int firstRead;

        internal Task ReadStarted => readStarted.Task;

        internal void Release() => release.TrySetResult();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await WaitForReleaseAsync(cancellationToken);
            return await inner.ReadAsync(buffer, cancellationToken);
        }

        public override async Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            await WaitForReleaseAsync(cancellationToken);
            return await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken);
        }

        private async Task WaitForReleaseAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref firstRead, 1) == 0)
            {
                readStarted.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            }
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException("The route must parse the multipart body asynchronously.");

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            GC.SuppressFinalize(this);
        }
    }
}
