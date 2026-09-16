using System.Text.Json;
using Dapper;
using MySqlConnector;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Projects;

namespace Yf.Api.Tests;

[Collection(ConnectionLifecycleCollection.Name)]
public sealed class MessageReceiptTests
{
    [Fact(Timeout = 60_000)]
    public async Task SupplierReadBecomesVisibleWithoutLeakingOtherProjects()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await LocalDatabaseScope.CreateOrSkipAsync("message_receipts", ct);
        await database.InitializeAsync(ct);
        await database.SeedAsync("""
            UPDATE users SET must_change_password=0 WHERE id=1;

            INSERT INTO suppliers(id,name,status,created_by)
            VALUES(100,'回执供应商','ACTIVE',1),(200,'其他供应商','ACTIVE',1);
            INSERT INTO roles(id,name,description,is_built_in,status)
            VALUES
                (9001,'回执内部成员','回执测试角色',0,'ACTIVE'),
                (9002,'回执供应商人员','回执测试角色',0,'ACTIVE'),
                (9003,'回执全局查看者','回执测试角色',0,'ACTIVE');
            INSERT INTO role_permissions(role_id,permission_id)
            SELECT test_roles.role_id,p.id
            FROM (SELECT 9001 AS role_id UNION ALL SELECT 9002 UNION ALL SELECT 9003) test_roles
            CROSS JOIN permissions p
            WHERE p.code='project:list'
               OR (test_roles.role_id IN (9001,9003) AND p.code='project:confirm')
               OR (test_roles.role_id=9003 AND p.code='project:view_all');
            INSERT INTO users
                (id,employee_no,password_hash,real_name,email,user_type,supplier_id,status,must_change_password)
            VALUES
                (101,'receipt-member','unused','项目成员','','INTERNAL',NULL,'ACTIVE',0),
                (102,'receipt-outsider','unused','项目外用户','','INTERNAL',NULL,'ACTIVE',0),
                (103,'receipt-old-member','unused','旧项目成员','','INTERNAL',NULL,'ACTIVE',0),
                (104,'receipt-view-all','unused','全局查看者','','INTERNAL',NULL,'ACTIVE',0),
                (201,'receipt-supplier','unused','供应商读者','','SUPPLIER',100,'ACTIVE',0),
                (202,'receipt-supplier-peer','unused','供应商同事','','SUPPLIER',100,'ACTIVE',0),
                (301,'receipt-other-supplier','unused','其他供应商用户','','SUPPLIER',200,'ACTIVE',0);
            INSERT INTO user_roles(user_id,role_id)
            VALUES(101,9001),(102,9001),(103,9001),(104,9003),(201,9002),(202,9002),(301,9002);
            INSERT INTO project_groups(id,name,supplier_id,status,created_by,responsible_user_id)
            VALUES(5001,'回执主项目',100,'IN_PROGRESS',1,101),(5002,'其他主项目',200,'IN_PROGRESS',102,102);
            INSERT INTO projects(id,project_group_id,name,supplier_id,status,created_by,responsible_user_id)
            VALUES
                (1001,5001,'回执项目',100,'IN_PROGRESS',1,101),
                (2001,5002,'其他项目',200,'IN_PROGRESS',102,102);
            INSERT INTO project_members(project_id,user_id,created_by)
            VALUES(1001,101,1),(1001,103,1);
            INSERT INTO messages(id,project_id,sender_id,content,status)
            VALUES
                (10001,1001,1,'不可由回执接口返回的正文','NORMAL'),
                (10002,1001,201,'供应商自己的留言','NORMAL'),
                (20001,2001,102,'其他项目正文','NORMAL');
            """, ct);

        var service = new MessageService(new AuditService([]), database.Options);
        var admin = new CurrentUser(1, "admin", "INTERNAL", null);
        var supplier = new CurrentUser(201, "receipt-supplier", "SUPPLIER", 100);
        var outsider = new CurrentUser(102, "receipt-outsider", "INTERNAL", null);

        await using var conn = await database.Database.OpenAsync(ct);
        var project = new ProjectRow { Id = 1001, SupplierId = 100, ResponsibleUserId = 101 };
        var participants = await ProjectNotificationService.ParticipantsAsync(conn, null, project, ct);
        Assert.Equal([101UL, 201UL, 202UL], participants.Select(user => user.Id).ToArray());
        var reviewers = await ProjectReviewerService.ListAsync(conn, null, project, ct);
        Assert.Contains(reviewers, user => user.Id == 101);
        Assert.Contains(reviewers, user => user.Id == 104);
        Assert.DoesNotContain(reviewers, user => user.Id == 103);
        await using (var authorizationTx = await AppDb.BeginTransactionAsync(conn, ct))
        {
            Assert.True(await ProjectNotificationService.IsCurrentProjectRecipientAsync(conn, authorizationTx, 1001, 101, ct));
            Assert.True(await ProjectNotificationService.IsCurrentProjectRecipientAsync(conn, authorizationTx, 1001, 104, ct));
            Assert.False(await ProjectNotificationService.IsCurrentProjectRecipientAsync(conn, authorizationTx, 1001, 103, ct));
            Assert.True(await ProjectNotificationService.IsCurrentProjectRecipientAsync(conn, authorizationTx, 1001, 201, ct));
            Assert.False(await ProjectNotificationService.IsCurrentProjectRecipientAsync(conn, authorizationTx, 1001, 301, ct));
            await authorizationTx.CommitAsync(ct);
        }

        var before = Json(await service.ReceiptsAsync(conn, admin, 1001, [10001], ct));
        var initialReceipt = Assert.Single(before.EnumerateArray());
        Assert.Equal(0, initialReceipt.GetProperty("readCount").GetInt32());
        Assert.Equal(3, initialReceipt.GetProperty("totalCount").GetInt32());
        Assert.False(initialReceipt.GetProperty("readByMe").GetBoolean());

        await service.MarkReadAsync(conn, supplier, new MarkMessagesReadRequest { Ids = [10001, 10002] }, ct);

        var after = Json(await service.ReceiptsAsync(conn, admin, 1001, [10001, 20001], ct));
        var receipt = Assert.Single(after.EnumerateArray());
        Assert.Equal(10001UL, receipt.GetProperty("id").GetUInt64());
        Assert.Equal(1, receipt.GetProperty("readCount").GetInt32());
        Assert.Equal(3, receipt.GetProperty("totalCount").GetInt32());
        Assert.False(receipt.GetProperty("readByMe").GetBoolean());
        Assert.Equal(
            ["id", "readByMe", "readCount", "totalCount"],
            receipt.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray());

        var supplierReceipt = Assert.Single(Json(
            await service.ReceiptsAsync(conn, supplier, 1001, [10001], ct)).EnumerateArray());
        Assert.True(supplierReceipt.GetProperty("readByMe").GetBoolean());

        var ownMessageReceipt = Assert.Single(Json(
            await service.ReceiptsAsync(conn, supplier, 1001, [10002], ct)).EnumerateArray());
        Assert.Equal(0, ownMessageReceipt.GetProperty("readCount").GetInt32());
        Assert.Equal(2, ownMessageReceipt.GetProperty("totalCount").GetInt32());
        Assert.False(ownMessageReceipt.GetProperty("readByMe").GetBoolean());

        var detailedReads = Json(await service.ReadsAsync(conn, admin, 10001, ct));
        Assert.Equal(
            receipt.GetProperty("totalCount").GetInt32(),
            detailedReads.GetProperty("readers").GetArrayLength()
            + detailedReads.GetProperty("unread").GetArrayLength());

        var denied = await Assert.ThrowsAsync<ApiException>(
            () => service.ReceiptsAsync(conn, outsider, 1001, [10001], ct));
        Assert.Equal(403, denied.Status);
        Assert.Empty(await service.ReceiptsAsync(conn, admin, 1001, [], ct));
    }

    [Fact]
    public void MessageIdQueryIsPositiveDeduplicatedAndBounded()
    {
        Assert.Empty(ProjectsModule.ParseMessageIds(null));
        Assert.Empty(ProjectsModule.ParseMessageIds(" "));
        Assert.Equal([1UL, 2UL, 3UL], ProjectsModule.ParseMessageIds("1, 2,1,3"));
        Assert.Equal(500, ProjectsModule.ParseMessageIds(
            string.Join(',', Enumerable.Range(1, 500))).Length);
        Assert.Equal([1UL], ProjectsModule.ParseMessageIds(
            string.Join(',', Enumerable.Repeat("1", 501))));

        foreach (var invalid in new[] { "0", "-1", "1,,2", "abc", "18446744073709551616" })
        {
            var error = Assert.Throws<ApiException>(() => ProjectsModule.ParseMessageIds(invalid));
            Assert.Equal(400, error.Status);
        }
        var tooMany = Assert.Throws<ApiException>(() => ProjectsModule.ParseMessageIds(
            string.Join(',', Enumerable.Range(1, 501))));
        Assert.Equal(400, tooMany.Status);
    }

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
                throw new InvalidOperationException("Message receipt tests only allow local MySQL");
            var credentials = uri.UserInfo.Split(':', 2);
            var administrationOptions = new MySqlConnectionStringBuilder
            {
                Server = uri.Host,
                Port = (uint)(uri.IsDefaultPort ? 3306 : uri.Port),
                UserID = Uri.UnescapeDataString(credentials[0]),
                Password = credentials.Length == 2 ? Uri.UnescapeDataString(credentials[1]) : string.Empty,
                DateTimeKind = MySqlDateTimeKind.Utc,
                SslMode = MySqlSslMode.None
            };
            var administration = new MySqlConnection(administrationOptions.ConnectionString);
            await administration.OpenAsync(ct);
            var databaseName = $"yf_test_dotnet_{purpose}_{Guid.NewGuid():N}";
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
                        MinimumPoolSize = 0
                    }.ConnectionString,
                    StorageRoot = Path.Combine(Path.GetTempPath(), "yf_message_receipt_storage"),
                    WorkerEnabled = false
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
            var previousPassword = Environment.GetEnvironmentVariable("YF_BOOTSTRAP_PASSWORD");
            Environment.SetEnvironmentVariable(
                "YF_BOOTSTRAP_PASSWORD",
                "Receipt#" + Guid.NewGuid().ToString("N")[..8] + "!");
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
            finally { await administration.DisposeAsync(); }
        }
    }
}
