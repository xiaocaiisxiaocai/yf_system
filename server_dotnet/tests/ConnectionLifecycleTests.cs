using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Identity;
using Yf.Api.Modules.SystemManagement;

namespace Yf.Api.Tests;

[CollectionDefinition("Connection lifecycle database tests", DisableParallelization = true)]
public sealed class ConnectionLifecycleCollectionDefinition
{
    public const string Name = "Connection lifecycle database tests";
}

[Collection(ConnectionLifecycleCollectionDefinition.Name)]
public sealed class ConnectionLifecycleTests
{
    [Theory(Timeout = 30_000)]
    [InlineData("/api/v1/admin/system/mail-settings")]
    [InlineData("/api/v1/admin/audit-logs")]
    public async Task SystemEndpointFilterReleasesPermissionConnectionBeforeHandler(string route)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await LocalDatabaseScope.CreateOrSkipAsync("endpoint", ct);
        await database.SeedAsync(HandwrittenTestSchemas.EndpointFilter + Environment.NewLine + """
            INSERT INTO roles VALUES(1,'系统管理员','ACTIVE',1);
            INSERT INTO permissions(id,code) VALUES(1,'config:manage'),(2,'log:view');
            INSERT INTO role_permissions VALUES(1,1),(1,2);
            INSERT INTO user_roles VALUES(1,1);
            INSERT INTO users VALUES(1,'pool_admin','连接池测试员');
            """, ct);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSingleton(database.Options);
        builder.Services.AddSingleton(database.Database);
        builder.Services.AddSingleton<AuditService>();
        builder.Services.AddSingleton<SystemService>();
        builder.Services.AddSingleton<SmtpSettingsService>();
        builder.Services.AddSingleton<SmtpTestService>();
        builder.Services.AddSingleton<MailService>();
        await using var application = builder.Build();
        application.MapSystemModule();
        var endpoint = ((IEndpointRouteBuilder)application).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(item => item.RoutePattern.RawText?.TrimEnd('/') == route.TrimEnd('/')
                && item.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains(HttpMethods.Get) == true);
        Assert.NotNull(endpoint.RequestDelegate);

        await using var requestScope = application.Services.CreateAsyncScope();
        var context = new DefaultHttpContext
        {
            RequestServices = requestScope.ServiceProvider,
            Response = { Body = new MemoryStream() }
        };
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = route;
        context.Items[typeof(CurrentUser)] = new CurrentUser(1, "pool_admin", "INTERNAL", null);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        context.RequestAborted = deadline.Token;

        await endpoint.RequestDelegate!(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.True(context.Response.Body.Length > 0);
    }

    [Fact(Timeout = 120_000)]
    public async Task EmptyDatabaseInitializationReleasesSeedConnectionAfterEfMigration()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await LocalDatabaseScope.CreateOrSkipAsync("bootstrap", ct);
        await SchemaBootstrap.InitializeEmptyAsync(database.Database, "PoolLifecycle#2026!", ct);

        await using var connection = await database.Database.OpenAsync(ct);
        Assert.Equal(EfDatabaseLifecycle.InitialMigrationId,
            await connection.ExecuteScalarAsync<string>(new CommandDefinition(
                "SELECT MigrationId FROM __EFMigrationsHistory", cancellationToken: ct)));
        Assert.False(await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM information_schema.tables WHERE table_schema=DATABASE() AND table_name='yf_schema_migrations')",
            cancellationToken: ct)));
        Assert.True(await connection.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS(
                SELECT 1 FROM information_schema.columns
                WHERE table_schema=DATABASE() AND table_name='refresh_tokens'
                    AND column_name='session_id' AND is_nullable='NO'
            )
            """, cancellationToken: ct)));
    }

    [Fact(Timeout = 120_000)]
    public async Task MinimalInitializationAndExplicitResetPreserveAdminPasswordAndSettings()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await LocalDatabaseScope.CreateOrSkipAsync("reset", ct);
        var root = Path.Combine(Path.GetTempPath(), "yf_reset_" + Guid.NewGuid().ToString("N"));
        database.Options.StorageRoot = root;
        database.Options.JwtSecret = new string('z', 48);
        await SchemaBootstrap.InitializeEmptyAsync(database.Database, "Reset#" + Guid.NewGuid().ToString("N")[..12], ct);
        Directory.CreateDirectory(Path.Combine(root, "files", "2026"));
        Directory.CreateDirectory(Path.Combine(root, "tmp", "owned"));
        await File.WriteAllTextAsync(Path.Combine(root, "files", "2026", "sample.pdf"), "owned file", ct);
        await File.WriteAllTextAsync(Path.Combine(root, "tmp", "owned", "0.part"), "owned chunk", ct);
        await File.WriteAllTextAsync(Path.Combine(root, "keep.txt"), "unmanaged file", ct);
        try
        {
            string passwordHash;
            int permissionCount;
            await using (var conn = await database.Database.OpenAsync(ct))
            {
                Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM users"));
                Assert.Equal("admin", await conn.ExecuteScalarAsync<string>("SELECT employee_no FROM users"));
                Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM roles"));
                Assert.Equal("系统管理员", await conn.ExecuteScalarAsync<string>("SELECT name FROM roles"));
                passwordHash = await conn.ExecuteScalarAsync<string>("SELECT password_hash FROM users") ?? throw new InvalidOperationException("Missing initialized password hash");
                permissionCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM permissions");
                await conn.ExecuteAsync("""
                    INSERT INTO roles(name,is_built_in,status) VALUES('待清理角色',0,'ACTIVE');
                    INSERT INTO users(employee_no,password_hash,real_name,email,user_type,status) SELECT 'discard',password_hash,'待清理用户','','INTERNAL','ACTIVE' FROM users WHERE employee_no='admin';
                    INSERT INTO user_roles(user_id,role_id) SELECT u.id,r.id FROM users u JOIN roles r ON r.name='待清理角色' WHERE u.employee_no='discard';
                    INSERT INTO suppliers(name) VALUES('待清理供应商');
                    INSERT INTO projects(name,supplier_id,created_by)
                    SELECT '待清理项目',s.id,u.id
                    FROM suppliers s JOIN users u ON u.employee_no='discard'
                    WHERE s.name='待清理供应商';
                    INSERT INTO messages(project_id,sender_id,content) SELECT p.id,p.created_by,'待清理留言' FROM projects p;
                    INSERT INTO oem_companies(name,status,created_at,updated_at)
                    VALUES('待清理 ODM/OEMA 厂商','ACTIVE',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
                    INSERT INTO oem_accounts(employee_no,password_hash,real_name,email,oem_company_id,status,must_change_password,failed_login_attempts,created_at,updated_at)
                    SELECT 'oem-discard','unused','待清理 ODM/OEMA 用户','oem-discard@example.invalid',id,'ACTIVE',1,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)
                    FROM oem_companies WHERE name='待清理 ODM/OEMA 厂商';
                    UPDATE system_configs SET cfg_value='false' WHERE cfg_key='notify.enabled';
                    INSERT INTO system_configs(cfg_key,cfg_value) VALUES('smtp.reset-test-secret','retained-test-value');
                    """);
            }
            var plan = await DevelopmentDataReset.InspectAsync(database.Options, ct);
            Assert.True(plan.RobotCatalogWillBeReinitialized);
            Assert.Equal(6, plan.RobotCatalogSupplierCount);
            Assert.Equal(40, plan.RobotCatalogPartCount);
            Assert.Equal(7, plan.Counts["suppliers"]);
            Assert.Equal(40, plan.Counts["robot_parts"]);
            Assert.Equal(1, plan.Counts["oem_companies"]);
            Assert.Equal(1, plan.Counts["oem_accounts"]);
            await Assert.ThrowsAsync<InvalidOperationException>(() => DevelopmentDataReset.ResetAsync(database.Options, "wrong-db", plan.StorageRoot, ct));
            Assert.True(File.Exists(Path.Combine(root, "files", "2026", "sample.pdf")));
            // Like the site, the lease holder uses its own non-pooled connection.
            await using (var app = new MySqlConnection(new MySqlConnectionStringBuilder(database.Options.ConnectionString) { Pooling = false }.ConnectionString))
            {
                await app.OpenAsync(ct);
                // A running site holds the application lease; reset must refuse before touching anything.
                await using var lease = await AppRunningLease.TryAcquireAsync(app, ct);
                Assert.NotNull(lease);
                var running = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    DevelopmentDataReset.ResetAsync(database.Options, plan.Database, plan.StorageRoot, ct));
                Assert.Contains("still running", running.Message);
                Assert.True(File.Exists(Path.Combine(root, "files", "2026", "sample.pdf")));
                await using var check = await database.Database.OpenAsync(ct);
                Assert.Equal(2, await check.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM users"));
            }
            var result = await DevelopmentDataReset.ResetAsync(database.Options, plan.Database, plan.StorageRoot, ct);
            Assert.True(result.ResetCompleted);
            Assert.Equal(1, result.Counts["users"]);
            Assert.Equal(1, result.Counts["roles"]);
            foreach (var table in new[] { "projects", "messages", "departments", "refresh_tokens", "audit_logs", "oem_companies", "oem_accounts" }) Assert.Equal(0, result.Counts[table]);
            Assert.Equal(6, result.Counts["suppliers"]);
            Assert.Equal(40, result.Counts["robot_parts"]);
            Assert.True(result.RobotCatalogWillBeReinitialized);
            Assert.Equal(6, result.RobotCatalogSupplierCount);
            Assert.Equal(40, result.RobotCatalogPartCount);
            Assert.Equal(permissionCount, result.Counts["permissions"]);
            Assert.Equal(permissionCount, result.Counts["role_permissions"]);
            Assert.Equal(1, result.Counts["user_roles"]);
            Assert.False(Directory.Exists(Path.Combine(root, "files")));
            Assert.False(Directory.Exists(Path.Combine(root, "tmp")));
            Assert.True(File.Exists(Path.Combine(root, "keep.txt")));
            await using (var conn = await database.Database.OpenAsync(ct))
            {
                Assert.Equal(passwordHash, await conn.ExecuteScalarAsync<string>("SELECT password_hash FROM users WHERE employee_no='admin'"));
                Assert.Equal("retained-test-value", await conn.ExecuteScalarAsync<string>("SELECT cfg_value FROM system_configs WHERE cfg_key='smtp.reset-test-secret'"));
                Assert.Equal("false", await conn.ExecuteScalarAsync<string>("SELECT cfg_value FROM system_configs WHERE cfg_key='notify.enabled'"));
                Assert.Equal("RESTORED", await conn.ExecuteScalarAsync<string>("SELECT cfg_value FROM system_configs WHERE cfg_key='oem.storage.reconcile_required'"));
                Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM oem_flow_templates WHERE id=1 AND is_default=1"));
                Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM oem_flow_template_nodes WHERE template_id=1 AND approver_source='DIRECT_LEADER'"));
                Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM oem_retention_templates WHERE id=1"));
                Assert.Equal(6, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM suppliers"));
                Assert.Equal(40, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM robot_parts"));
            }
            var repeated = await DevelopmentDataReset.ResetAsync(database.Options, plan.Database, plan.StorageRoot, ct);
            Assert.True(repeated.ResetCompleted);
            Assert.Equal(6, repeated.Counts["suppliers"]);
            Assert.Equal(40, repeated.Counts["robot_parts"]);
            await SchemaBootstrap.ValidateAsync(database.Database, ct);
        }
        finally
        {
            Yf.Api.Modules.Files.FileStorage.DeleteDirectoryTree(Path.GetTempPath(), root, CancellationToken.None);
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task ResetClearsOemContactsOutsourceDataAndDataScopesWithoutForeignKeyConflicts()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await LocalDatabaseScope.CreateOrSkipAsync("reset_fk_all", ct);
        var root = Path.Combine(Path.GetTempPath(), "yf_reset_fk_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        database.Options.StorageRoot = root;
        database.Options.JwtSecret = new string('z', 48);
        await SchemaBootstrap.InitializeEmptyAsync(database.Database, "Reset#" + Guid.NewGuid().ToString("N")[..12], ct);
        try
        {
            int seededOutsourceFieldCount;
            await using (var conn = await database.Database.OpenAsync(ct))
            {
                seededOutsourceFieldCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM outsource_fields");
                Assert.True(seededOutsourceFieldCount > 0);
                await conn.ExecuteAsync("""
                    INSERT INTO roles(name,is_built_in,status) VALUES('重置角色',0,'ACTIVE');
                    INSERT INTO announcements(created_by,department_name,title,body,internal_scope,status,version,created_at,updated_at)
                    SELECT id,'','重置公告','','OWN','DRAFT',1,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6)
                    FROM users WHERE employee_no='admin';
                    INSERT INTO announcement_reads(announcement_id,realm,account_id,account_name,display_name,first_read_at,last_read_at)
                    SELECT id,'INTERNAL',created_by,'admin','管理员',UTC_TIMESTAMP(6),UTC_TIMESTAMP(6)
                    FROM announcements WHERE title='重置公告';
                    INSERT INTO announcement_assets(kind,file_name,content_type,size_bytes,sha256,storage_path,created_at)
                    VALUES('ATTACHMENT','packing.pdf','application/pdf',1,REPEAT('a',64),'announcement-assets/test/packing.pdf',UTC_TIMESTAMP(6));
                    INSERT INTO announcement_asset_links(announcement_id,asset_id,sort_order,created_at)
                    SELECT a.id,f.id,0,UTC_TIMESTAMP(6) FROM announcements a CROSS JOIN announcement_assets f
                    WHERE a.title='重置公告' AND f.file_name='packing.pdf';
                    INSERT INTO role_data_scopes(role_id,`domain`,`scope`)
                    SELECT id,'project','ORG' FROM roles WHERE name='重置角色';
                    INSERT INTO users(employee_no,password_hash,real_name,email,user_type,status)
                    SELECT 'reset-user',password_hash,'重置用户','reset@example.invalid','INTERNAL','ACTIVE'
                    FROM users WHERE employee_no='admin';
                    INSERT INTO user_roles(user_id,role_id)
                    SELECT u.id,r.id FROM users u JOIN roles r ON r.name='重置角色' WHERE u.employee_no='reset-user';
                    INSERT INTO user_data_scopes(user_id,`domain`,`scope`)
                    SELECT id,'project','ALL' FROM users WHERE employee_no='reset-user';

                    INSERT INTO suppliers(name) VALUES('重置供应商');
                    INSERT INTO projects(name,supplier_id,created_by)
                    SELECT '重置项目',s.id,u.id FROM suppliers s JOIN users u ON u.employee_no='reset-user' WHERE s.name='重置供应商';
                    INSERT INTO project_work_orders(project_id,work_order_no,sort_no)
                    SELECT id,'WO-RESET-001',0 FROM projects WHERE name='重置项目';

                    INSERT INTO oem_companies(name,status,created_at,updated_at)
                    VALUES('重置外协厂商','ACTIVE',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
                    INSERT INTO oem_company_contacts(oem_company_id,name,email,status,created_at,updated_at)
                    SELECT id,'外协联络人','contact@oem.invalid','ACTIVE',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)
                    FROM oem_companies WHERE name='重置外协厂商';
                    INSERT INTO oem_accounts(employee_no,password_hash,real_name,email,oem_company_id,status,must_change_password,failed_login_attempts,created_at,updated_at)
                    SELECT 'oem-reset','unused','重置OEM账号','oem-reset@oem.invalid',id,'ACTIVE',1,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)
                    FROM oem_companies WHERE name='重置外协厂商';
                    INSERT INTO oem_internal_recipient_exclusions(user_id,created_at)
                    SELECT id,UTC_TIMESTAMP(3) FROM users WHERE employee_no='reset-user';

                    INSERT INTO outsource_types(code,name,status,sort_no) VALUES('RESET_TYPE','重置外发类型','ACTIVE',1);
                    INSERT INTO outsource_type_vendors(type_id,vendor_id)
                    SELECT t.id,c.id FROM outsource_types t JOIN oem_companies c ON c.name='重置外协厂商' WHERE t.code='RESET_TYPE';
                    INSERT INTO outsource_vendor_emails(vendor_id,email,created_at,updated_at)
                    SELECT id,'outsource@oem.invalid',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)
                    FROM oem_companies WHERE name='重置外协厂商';
                    INSERT INTO outsource_default_cc(user_id,sort_no)
                    SELECT id,1 FROM users WHERE employee_no='reset-user';
                    INSERT INTO outsource_default_cc_modules(user_id,module)
                    SELECT id,'LIST' FROM users WHERE employee_no='reset-user';
                    INSERT INTO outsource_customer_aliases(full_name,short_name,manual,updated_at)
                    VALUES('重置客户全称','重置客户',1,UTC_TIMESTAMP(3));
                    INSERT INTO outsource_items(work_order_no,serial_no,outsource_date,vendor_id,status,send_status)
                    SELECT 'OS-WO-001',1,'2026-11-25',id,'NORMAL','UNSENT'
                    FROM oem_companies WHERE name='重置外协厂商';
                    INSERT INTO outsource_item_changes(item_id,field,old_value,new_value,source)
                    SELECT id,'remark','','测试','EDIT' FROM outsource_items WHERE work_order_no='OS-WO-001';
                    INSERT INTO outsource_item_values(item_id,field_id,value)
                    SELECT i.id,f.id,'true' FROM outsource_items i JOIN outsource_fields f ON f.field_key='hasRobotV'
                    WHERE i.work_order_no='OS-WO-001';
                    INSERT INTO outsource_mail_batches(request_key,created_by)
                    SELECT 'reset-mail-batch-1',id FROM users WHERE employee_no='reset-user';
                    INSERT INTO outsource_mails(batch_key,kind,vendor_id,subject,body_html,body_text,status)
                    SELECT 'reset-mail-batch-1','NOTICE',id,'外协通知','<p>ok</p>','ok','SENT'
                    FROM oem_companies WHERE name='重置外协厂商';
                    INSERT INTO outsource_mail_recipients(mail_id,kind,email,source)
                    SELECT id,'TO','outsource@oem.invalid','VENDOR' FROM outsource_mails WHERE batch_key='reset-mail-batch-1';
                    INSERT INTO outsource_mail_items(mail_id,item_id,work_order_no)
                    SELECT m.id,i.id,i.work_order_no FROM outsource_mails m JOIN outsource_items i ON i.work_order_no='OS-WO-001'
                    WHERE m.batch_key='reset-mail-batch-1';
                    INSERT INTO outsource_field_editor_roles(field_id,role_id)
                    SELECT f.id,r.id FROM outsource_fields f JOIN roles r ON r.name='重置角色' WHERE f.field_key='hasRobotV';
                    INSERT INTO outsource_field_editor_users(field_id,user_id)
                    SELECT f.id,u.id FROM outsource_fields f JOIN users u ON u.employee_no='reset-user' WHERE f.field_key='hasRobotV';
                    INSERT INTO mes_work_orders(work_order_no,batch_id,fetched_at,raw_json)
                    VALUES('MES-WO-001','batch-1',UTC_TIMESTAMP(3),'{"planEndDate":"2026-12-01"}');
                    INSERT INTO mes_work_orders_staging(work_order_no,batch_id,fetched_at,raw_json,seq)
                    VALUES('MES-WO-001','batch-1',UTC_TIMESTAMP(3),'{"planEndDate":"2026-12-01"}',1);

                    INSERT INTO oem_transfers(direction,oem_company_id,outsource_item_id,title,lifecycle_status,retention_template_id,created_at,updated_at)
                    SELECT 'INTERNAL_TO_OEM',c.id,i.id,'关联外协传递单','DRAFT',1,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)
                    FROM oem_companies c JOIN outsource_items i ON i.work_order_no='OS-WO-001' WHERE c.name='重置外协厂商';
                    INSERT INTO oem_transfer_internal_recipients(transfer_id,user_id)
                    SELECT t.id,u.id FROM oem_transfers t JOIN users u ON u.employee_no='reset-user' WHERE t.title='关联外协传递单';
                    """);
            }

            var plan = await DevelopmentDataReset.InspectAsync(database.Options, ct);
            Assert.Equal(1, plan.Counts["oem_company_contacts"]);
            Assert.Equal(1, plan.Counts["oem_transfers"]);
            Assert.Equal(1, plan.Counts["oem_transfer_internal_recipients"]);
            Assert.Equal(1, plan.Counts["oem_internal_recipient_exclusions"]);
            Assert.Equal(1, plan.Counts["outsource_items"]);
            Assert.Equal(1, plan.Counts["outsource_mails"]);
            Assert.Equal(1, plan.Counts["outsource_type_vendors"]);
            Assert.Equal(1, plan.Counts["outsource_vendor_emails"]);
            Assert.Equal(1, plan.Counts["user_data_scopes"]);
            Assert.Equal(1, plan.Counts["announcements"]);
            Assert.Equal(1, plan.Counts["announcement_reads"]);
            Assert.Equal(1, plan.Counts["announcement_assets"]);
            Assert.Equal(1, plan.Counts["announcement_asset_links"]);
            Assert.Equal(1, plan.Counts["project_work_orders"]);

            var announcementDirectory = Path.Combine(root, "announcement-assets", "test");
            Directory.CreateDirectory(announcementDirectory);
            await File.WriteAllBytesAsync(Path.Combine(announcementDirectory, "packing.pdf"), [42], ct);
            var result = await DevelopmentDataReset.ResetAsync(database.Options, plan.Database, plan.StorageRoot, ct);
            Assert.True(result.ResetCompleted);
            Assert.False(Directory.Exists(announcementDirectory));
            foreach (var table in new[]
            {
                "oem_companies", "oem_company_contacts", "oem_transfers", "oem_transfer_internal_recipients",
                "announcements", "announcement_reads", "announcement_departments", "announcement_companies",
                "announcement_assets", "announcement_asset_links",
                "oem_internal_recipient_exclusions", "outsource_mail_items", "outsource_mail_recipients",
                "outsource_mails", "outsource_mail_batches", "outsource_item_changes", "outsource_item_values",
                "outsource_items", "outsource_type_vendors", "outsource_vendor_emails",
                "outsource_default_cc_modules", "outsource_default_cc", "outsource_customer_aliases",
                "outsource_field_editor_roles", "outsource_field_editor_users",
                "mes_work_orders_staging", "mes_work_orders", "project_work_orders", "user_data_scopes"
            })
            {
                Assert.Equal(0, result.Counts[table]);
            }
            Assert.Equal(DataScopeDomains.All.Length, result.Counts["role_data_scopes"]);

            await using var verify = await database.Database.OpenAsync(ct);
            Assert.Equal(seededOutsourceFieldCount, await verify.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM outsource_fields"));
            Assert.Equal(1, await verify.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM outsource_types WHERE code='RESET_TYPE'"));
            Assert.Equal(DataScopeDomains.All.Length, await verify.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM role_data_scopes WHERE role_id=1 AND `scope`='ALL'"));
        }
        finally
        {
            Yf.Api.Modules.Files.FileStorage.DeleteDirectoryTree(Path.GetTempPath(), root, CancellationToken.None);
        }
    }

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
            var administrationOptions = LocalMySqlTestServer.AdministrationOrSkip("Connection lifecycle tests");
            var administration = new MySqlConnection(administrationOptions.ConnectionString);
            await administration.OpenAsync(ct);
            var databaseName = $"yf_t_{Guid.NewGuid():N}";
            try
            {
                await administration.ExecuteAsync(new CommandDefinition(
                    $"CREATE DATABASE `{databaseName}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci",
                    cancellationToken: ct));
                var applicationOptions = new AppOptions
                {
                    ConnectionString = new MySqlConnectionStringBuilder(administrationOptions.ConnectionString)
                    {
                        Database = databaseName,
                        MaximumPoolSize = 1,
                        MinimumPoolSize = 0
                    }.ConnectionString,
                    StorageRoot = Path.Combine(Path.GetTempPath(), "yf_connection_lifecycle_storage"),
                    WorkerEnabled = false
                };
                return new(administration, databaseName, new AppDb(applicationOptions), applicationOptions);
            }
            catch
            {
                try { await administration.ExecuteAsync($"DROP DATABASE IF EXISTS `{databaseName}`"); }
                finally { await administration.DisposeAsync(); }
                throw;
            }
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
