using Dapper;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Admin;
using Yf.Api.Modules.Identity;

namespace Yf.Api.Tests;

public sealed class AccountDeletionHistoryTests
{
    [Fact(Timeout = 90_000)]
    public async Task SoleUnusedSupplierAccountAndThenItsSupplierCanBeDeleted()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("sole_supplier_account_delete", ct);
        await database.InitializeBusinessFixtureAsync(ct);
        await SchemaMigrations.ApplyAsync(database.Database, ct);
        await database.ExecuteAsync("""
            INSERT INTO suppliers(id,name,status,created_at,updated_at)
            VALUES(9701,'唯一空账号供应商','ACTIVE',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
            INSERT INTO users(id,employee_no,password_hash,real_name,email,user_type,supplier_id,status,must_change_password,failed_login_attempts,created_at,updated_at)
            VALUES(9700,'sole-delete-admin','unused','删除管理员','admin@example.invalid','INTERNAL',NULL,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
                  (9702,'sole-unused-supplier','unused','唯一空账号','supplier@example.invalid','SUPPLIER',9701,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
            INSERT INTO user_roles(user_id,role_id) VALUES(9700,1),(9702,4);
            """, ct);
        var suppliers = new SupplierService(EfTestSupport.DbContextFactory(database.Options), new PermissionService(), new AuditService([]));
        var admin = new CurrentUser(9700, "sole-delete-admin", "INTERNAL", null);

        await suppliers.DeleteAccountAsync(admin, 9702, ct);
        await suppliers.DeleteAsync(admin, 9701, ct);

        await using var connection = await database.Database.OpenAsync(ct);
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM users WHERE id=9702", cancellationToken: ct)));
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM suppliers WHERE id=9701", cancellationToken: ct)));
    }

    [Fact(Timeout = 90_000)]
    public async Task MailRecordsAndAuditLogsDoNotBlockSupplierAccountDeletion()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("account_delete_logs", ct);
        await database.InitializeBusinessFixtureAsync(ct);
        await SchemaMigrations.ApplyAsync(database.Database, ct);
        await database.ExecuteAsync("""
            INSERT INTO suppliers(id,name,status,created_at,updated_at)
            VALUES(9601,'日志供应商','ACTIVE',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
            INSERT INTO users(id,employee_no,password_hash,real_name,email,user_type,supplier_id,status,must_change_password,failed_login_attempts,created_at,updated_at)
            VALUES(9600,'log-delete-admin','unused','删除管理员','admin@example.invalid','INTERNAL',NULL,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
                  (9602,'log-only-supplier','unused','日志账号','log@example.test','SUPPLIER',9601,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
                  (9603,'uploader-supplier','unused','上传账号','up@example.test','SUPPLIER',9601,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
            INSERT INTO user_roles(user_id,role_id) VALUES(9600,1),(9602,4),(9603,4);
            INSERT INTO audit_logs(user_id,employee_no,action,created_at)
            VALUES(9602,'log-only-supplier','LOGIN',UTC_TIMESTAMP(6)),(9602,'log-only-supplier','PASSWORD_CHANGE',UTC_TIMESTAMP(6));
            INSERT INTO email_outbox(event_type,recipient_user_id,recipient_email,subject,body,status,retry_count,created_at)
            VALUES('FILE_UPLOADED',9602,'contact@example.test','已发送','正文','SENT',0,UTC_TIMESTAMP(3)),
                  ('MESSAGE_CREATED',9602,'contact@example.test','待发送','正文','PENDING',0,UTC_TIMESTAMP(3));
            """, ct);
        var suppliers = new SupplierService(EfTestSupport.DbContextFactory(database.Options), new PermissionService(), new AuditService([]));
        var admin = new CurrentUser(9600, "log-delete-admin", "INTERNAL", null);
        await suppliers.DeleteAccountAsync(admin, 9602, ct);
        // The test pool has one connection; release it before calling the service again.
        await using (var conn = await database.Database.OpenAsync(ct))
        {
            Assert.Equal(0, await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM users WHERE id=9602", cancellationToken: ct)));
            // Logs are kept; only undelivered mail is cancelled.
            Assert.Equal(2, await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM audit_logs WHERE user_id=9602", cancellationToken: ct)));
            Assert.Equal(["CANCELLED", "SENT"], (await conn.QueryAsync<string>(new CommandDefinition(
                "SELECT status FROM email_outbox WHERE recipient_user_id=9602 ORDER BY status", cancellationToken: ct))).ToArray());

            // Real business content still blocks deletion.
            await conn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO project_groups(id,name,supplier_id,status,created_by,machine_model,created_at,updated_at)
                VALUES(9611,'日志主项目',9601,'DRAFT',9600,'机型',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
                INSERT INTO projects(id,project_group_id,name,supplier_id,status,created_by,machine_model,created_at,updated_at)
                VALUES(9612,9611,'日志子项目',9601,'TERMINATED',9600,'机型',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
                INSERT INTO messages(id,project_id,sender_id,content,status,created_at)
                VALUES(9613,9612,9603,'供应商留言','NORMAL',UTC_TIMESTAMP(3));
                """, cancellationToken: ct));
        }
        var blocked = await Assert.ThrowsAsync<ApiException>(() => suppliers.DeleteAccountAsync(admin, 9603, ct));
        Assert.Equal("该账号仍有业务或历史记录，请禁用账号，不要删除", blocked.Message);
    }
}
