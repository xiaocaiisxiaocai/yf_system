using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Http;
using MySqlConnector;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Projects;

namespace Yf.Api.Tests;

[Collection(ConnectionLifecycleCollection.Name)]
public sealed class MessageImageTests
{
    [Theory]
    [InlineData("png", "89504e470d0a1a0a", true)]
    [InlineData("jpg", "ffd8ffe0", true)]
    [InlineData("jpeg", "ffd8ffdb", true)]
    [InlineData("gif", "474946383961", true)]
    [InlineData("webp", "524946460000000057454250", true)]
    [InlineData("bmp", "424d", true)]
    [InlineData("png", "3c737667", false)]
    [InlineData("webp", "52494646000000004e4f5045", false)]
    public void ImageSignaturesMustMatchExtension(string extension, string hex, bool expected)
    {
        Assert.Equal(expected, MessageService.HasExpectedSignature(extension, Convert.FromHexString(hex)));
    }

    [Fact(Timeout = 90_000)]
    public async Task ImagesAreAtomicPrivateAndRespectSystemLimit()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await LocalDatabaseScope.CreateOrSkipAsync("message_images", ct);
        await database.InitializeAsync(ct);
        await database.SeedAsync("""
            UPDATE users SET must_change_password=0 WHERE id=1;
            INSERT INTO suppliers(id,name,status,created_by) VALUES(100,'图片供应商','ACTIVE',1);
            INSERT INTO roles(id,name,description,is_built_in,status) VALUES(9001,'图片范围用户','图片测试',0,'ACTIVE');
            INSERT INTO role_permissions(role_id,permission_id)
            SELECT 9001,id FROM permissions WHERE code='project:list';
            INSERT INTO users(id,employee_no,password_hash,real_name,email,user_type,status,must_change_password)
            VALUES(101,'image-outsider','unused','范围外用户','','INTERNAL','ACTIVE',0);
            INSERT INTO user_roles(user_id,role_id) VALUES(101,9001);
            INSERT INTO projects(id,name,supplier_id,status,created_by)
            VALUES
                (1001,'图片项目',100,'IN_PROGRESS',1),
                (1002,'已完成图片项目',100,'COMPLETED',1);
            """, ct);

        var admin = new CurrentUser(1, "admin", "INTERNAL", null);
        var outsider = new CurrentUser(101, "image-outsider", "INTERNAL", null);
        var service = new MessageService(new AuditService([]), database.Options);
        await using var conn = await database.Database.OpenAsync(ct);

        Assert.Equal(SchemaMigrations.CurrentVersion, await conn.ExecuteScalarAsync<int>(
            "SELECT MAX(version) FROM yf_schema_migrations"));
        Assert.True(await conn.ExecuteScalarAsync<bool>(
            "SELECT EXISTS(SELECT 1 FROM information_schema.tables WHERE table_schema=DATABASE() AND table_name='message_images')"));

        var png = Convert.FromHexString("89504e470d0a1a0a0000000d49484452");
        var created = Json(await service.CreateAsync(conn, admin, 1001,
            new MessageCreateRequest { Content = "" },
            [File("clipboard.png", "image/png", png)], null, ct));
        Assert.Equal(string.Empty, created.GetProperty("content").GetString());
        var image = Assert.Single(created.GetProperty("images").EnumerateArray());
        Assert.Equal("clipboard.png", image.GetProperty("name").GetString());
        Assert.Equal("image/png", image.GetProperty("mimeType").GetString());
        Assert.Equal((ulong)png.Length, image.GetProperty("sizeBytes").GetUInt64());
        var messageId = created.GetProperty("id").GetUInt64();
        var imageId = image.GetProperty("id").GetUInt64();

        var listed = Json(await service.ListAsync(conn, admin, 1001, 1, 20, null, null, ct));
        Assert.Single(Assert.Single(listed.GetProperty("list").EnumerateArray())
            .GetProperty("images").EnumerateArray());
        var download = await service.GetImageAsync(conn, admin, messageId, imageId, ct);
        Assert.Equal(png, await System.IO.File.ReadAllBytesAsync(download.Path, ct));
        Assert.Equal("image/png", download.MimeType);

        var dashboard = Json(await new DashboardService().MessagesAsync(conn, admin, 1, 20, false, ct));
        Assert.Equal("[图片]", Assert.Single(dashboard.GetProperty("list").EnumerateArray())
            .GetProperty("content").GetString());

        var denied = await Assert.ThrowsAsync<ApiException>(
            () => service.GetImageAsync(conn, outsider, messageId, imageId, ct));
        Assert.Equal(403, denied.Status);

        var beforeFiles = StoredFiles(database.Options.StorageRoot);
        var beforeMessages = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM messages");
        var corrupt = await Assert.ThrowsAsync<ApiException>(() => service.CreateAsync(conn, admin, 1001,
            new MessageCreateRequest { Content = "must rollback" },
            [File("first.png", "image/png", png), File("second.png", "image/png", "<svg>"u8.ToArray())],
            null, ct));
        Assert.Equal(400, corrupt.Status);
        Assert.Equal(beforeMessages, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM messages"));
        Assert.Equal(beforeFiles, StoredFiles(database.Options.StorageRoot));

        var completed = await Assert.ThrowsAsync<ApiException>(() => service.CreateAsync(conn, admin, 1002,
            new MessageCreateRequest { Content = "race-safe" },
            [File("completed.png", "image/png", png)], null, ct));
        Assert.Equal(409, completed.Status);
        Assert.Equal(beforeFiles, StoredFiles(database.Options.StorageRoot));

        await conn.ExecuteAsync("UPDATE system_configs SET cfg_value='7' WHERE cfg_key='upload.max_file_size'");
        var overSystemLimit = await Assert.ThrowsAsync<ApiException>(() => service.CreateAsync(conn, admin, 1001,
            new MessageCreateRequest { Content = null },
            [File("limited.png", "image/png", png)], null, ct));
        Assert.Equal(400, overSystemLimit.Status);
        Assert.Equal(beforeFiles, StoredFiles(database.Options.StorageRoot));

        await conn.ExecuteAsync("UPDATE messages SET status='DELETED' WHERE id=@Id", new { Id = messageId });
        var deleted = await Assert.ThrowsAsync<ApiException>(
            () => service.GetImageAsync(conn, admin, messageId, imageId, ct));
        Assert.Equal(404, deleted.Status);
    }

    [Fact]
    public void MultipartLimitOnlyMatchesTheExactMessageCreateRoute()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.ContentType = "multipart/form-data; boundary=test";
        context.Request.Path = "/api/v1/projects/42/messages";
        Assert.True(ProjectsModule.IsMessageImageUpload(context.Request));
        context.Request.Path = "/api/v1/projects/42/files";
        Assert.False(ProjectsModule.IsMessageImageUpload(context.Request));
        context.Request.Path = "/api/v1/projects/42/messages/extra";
        Assert.False(ProjectsModule.IsMessageImageUpload(context.Request));
        context.Request.Path = "/api/v1/projects/42/messages";
        context.Request.Method = HttpMethods.Put;
        Assert.False(ProjectsModule.IsMessageImageUpload(context.Request));
    }

    private static FormFile File(string name, string contentType, byte[] bytes) => new(
        new MemoryStream(bytes, writable: false), 0, bytes.Length, "images", name)
    {
        Headers = new HeaderDictionary(),
        ContentType = contentType,
    };

    private static string[] StoredFiles(string root) => Directory.Exists(Path.Combine(root, "message-images"))
        ? Directory.GetFiles(Path.Combine(root, "message-images"), "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).ToArray()
        : [];

    private static JsonElement Json(object value) =>
        JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web);

    private sealed class LocalDatabaseScope(
        MySqlConnection administration,
        string databaseName,
        AppDb database,
        AppOptions options) : IAsyncDisposable
    {
        public AppDb Database { get; } = database;
        public AppOptions Options { get; } = options;

        public static async Task<LocalDatabaseScope> CreateOrSkipAsync(string purpose, CancellationToken ct)
        {
            var raw = Environment.GetEnvironmentVariable("YF_TEST_DATABASE_URL");
            if (string.IsNullOrWhiteSpace(raw)) Assert.Skip("YF_TEST_DATABASE_URL is not set");
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.Scheme != "mysql")
                throw new InvalidOperationException("YF_TEST_DATABASE_URL must be a mysql:// URL");
            if (uri.Host is not ("127.0.0.1" or "localhost" or "::1"))
                throw new InvalidOperationException("Message image tests only allow local MySQL");
            var credentials = uri.UserInfo.Split(':', 2);
            var administrationOptions = new MySqlConnectionStringBuilder
            {
                Server = uri.Host,
                Port = (uint)(uri.IsDefaultPort ? 3306 : uri.Port),
                UserID = Uri.UnescapeDataString(credentials[0]),
                Password = credentials.Length == 2 ? Uri.UnescapeDataString(credentials[1]) : string.Empty,
                DateTimeKind = MySqlDateTimeKind.Utc,
                SslMode = MySqlSslMode.None,
            };
            var administration = new MySqlConnection(administrationOptions.ConnectionString);
            await administration.OpenAsync(ct);
            var databaseName = $"yf_test_dotnet_{purpose}_{Guid.NewGuid():N}";
            var storage = Path.Combine(Path.GetTempPath(), "yf_message_images_" + Guid.NewGuid().ToString("N"));
            try
            {
                await administration.ExecuteAsync(new CommandDefinition(
                    $"CREATE DATABASE `{databaseName}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci",
                    cancellationToken: ct));
                var options = new AppOptions
                {
                    ConnectionString = new MySqlConnectionStringBuilder(administrationOptions.ConnectionString)
                    {
                        Database = databaseName,
                        MaximumPoolSize = 1,
                        MinimumPoolSize = 0,
                    }.ConnectionString,
                    StorageRoot = storage,
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

        public async Task InitializeAsync(CancellationToken ct)
        {
            Directory.CreateDirectory(Options.StorageRoot);
            var previousPassword = Environment.GetEnvironmentVariable("YF_BOOTSTRAP_PASSWORD");
            Environment.SetEnvironmentVariable("YF_BOOTSTRAP_PASSWORD", "Images#" + Guid.NewGuid().ToString("N")[..8]);
            try { await SchemaBootstrap.InitializeEmptyAsync(Database, ct); }
            finally { Environment.SetEnvironmentVariable("YF_BOOTSTRAP_PASSWORD", previousPassword); }
        }

        public async Task SeedAsync(string sql, CancellationToken ct)
        {
            await using var connection = await Database.OpenAsync(ct);
            await connection.ExecuteAsync(new CommandDefinition(sql, cancellationToken: ct));
        }

        public async ValueTask DisposeAsync()
        {
            try { await administration.ExecuteAsync($"DROP DATABASE IF EXISTS `{databaseName}`"); }
            finally
            {
                await administration.DisposeAsync();
                if (Directory.Exists(Options.StorageRoot)) Directory.Delete(Options.StorageRoot, recursive: true);
            }
        }
    }
}
