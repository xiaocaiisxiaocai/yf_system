using System.Net;
using Dapper;

namespace Yf.Api.Tests.Oem;

public sealed class OemDirectoryDeletionTests
{
    [Fact(Timeout = 180_000)]
    public async Task EmptyAccountAndCompanyCanBeDeletedWithSessionsAndAuditSnapshotsHandled()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var admin = await host.LoginAdminAsync(ct);
        var companyId = (await admin.PostAsync("/api/v1/oem/companies", new { name = "可删除空厂商" }, ct).Ok()).Id();
        var accountId = await CreateAccountAsync(admin, companyId, "delete_empty", ct);

        await using (var connection = await host.OpenAsync(ct))
        {
            await connection.ExecuteAsync("""
                INSERT INTO oem_refresh_tokens(account_id,session_id,token_hash,session_expires_at,expires_at,revoked,created_at)
                VALUES(@accountId,'deletion-session',REPEAT('a',64),UTC_TIMESTAMP(3) + INTERVAL 1 DAY,UTC_TIMESTAMP(3) + INTERVAL 1 DAY,0,UTC_TIMESTAMP(3))
                """, new { accountId });
        }

        // The only active account of an active vendor is protected; retiring the vendor first lifts that.
        await admin.DeleteAsync($"/api/v1/oem/accounts/{accountId}", ct).Status(HttpStatusCode.Conflict, 40901);
        await admin.PutAsync($"/api/v1/oem/companies/{companyId}/status", new { status = "DISABLED" }, ct).Ok();
        await admin.DeleteAsync($"/api/v1/oem/accounts/{accountId}", ct).Ok();
        await using (var connection = await host.OpenAsync(ct))
        {
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM oem_accounts WHERE id=@accountId", new { accountId }));
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM oem_refresh_tokens WHERE account_id=@accountId", new { accountId }));
            var detail = await connection.ExecuteScalarAsync<string>(
                "SELECT detail FROM audit_logs WHERE action='OEM_ACCOUNT_DELETE' AND target_id=@targetId ORDER BY id DESC LIMIT 1",
                new { targetId = accountId.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            Assert.NotNull(detail);
            Assert.Contains("删除测试账号", detail);
            Assert.Contains("delete_empty", detail);
        }

        await admin.DeleteAsync($"/api/v1/oem/companies/{companyId}", ct).Ok();
        await using (var connection = await host.OpenAsync(ct))
        {
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM oem_companies WHERE id=@companyId", new { companyId }));
            var detail = await connection.ExecuteScalarAsync<string>(
                "SELECT detail FROM audit_logs WHERE action='OEM_COMPANY_DELETE' AND target_id=@targetId ORDER BY id DESC LIMIT 1",
                new { targetId = companyId.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            Assert.NotNull(detail);
            Assert.Contains("可删除空厂商", detail);
        }
    }

    [Fact(Timeout = 180_000)]
    public async Task EveryOemAccountHistoryReferenceAndCompanyTransferRejectDeletionAtomically()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var admin = await host.LoginAdminAsync(ct);
        var companyId = (await admin.PostAsync("/api/v1/oem/companies", new { name = "历史引用厂商" }, ct).Ok()).Id();
        var transferOnlyCompanyId = (await admin.PostAsync("/api/v1/oem/companies", new { name = "仅传递历史厂商" }, ct).Ok()).Id();
        var ids = new ulong[8];
        for (var i = 0; i < ids.Length; i++) ids[i] = await CreateAccountAsync(admin, companyId, $"history_{i}", ct);

        await using (var connection = await host.OpenAsync(ct))
        {
            var retentionId = await connection.ExecuteScalarAsync<ulong>("SELECT id FROM oem_retention_templates ORDER BY id LIMIT 1");
            await connection.ExecuteAsync("""
                INSERT INTO oem_transfers(
                    direction,oem_company_id,title,lifecycle_status,retention_template_id,created_at,updated_at,concurrency_version)
                VALUES('INTERNAL_TO_OEM',@transferOnlyCompanyId,'无账号但有传递历史','DRAFT',@retentionId,
                    UTC_TIMESTAMP(3),UTC_TIMESTAMP(3),0)
                """, new { transferOnlyCompanyId, retentionId });
            var transferId = await connection.ExecuteScalarAsync<ulong>("""
                INSERT INTO oem_transfers(
                    direction,oem_company_id,title,oem_sender_account_id,lifecycle_status,retention_template_id,
                    closed_by_realm,closed_by_id,created_at,updated_at,concurrency_version)
                VALUES('OEM_TO_INTERNAL',@companyId,'删除引用保护',@senderId,'DRAFT',@retentionId,
                    'oem',@closedById,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3),0);
                SELECT LAST_INSERT_ID();
                """, new { companyId, senderId = ids[0], closedById = ids[1], retentionId });
            var fileId = await connection.ExecuteScalarAsync<ulong>("""
                INSERT INTO oem_transfer_files(
                    transfer_id,uploaded_by_oem_account_id,original_name,stored_name,ext,size_bytes,sha256,storage_path,
                    payload_status,scan_status,first_recipient_download_at,first_recipient_realm,first_recipient_id,
                    purge_attempt_count,concurrency_version,created_at,updated_at)
                VALUES(@transferId,@uploadedById,'history.pdf','history-file','pdf',1,REPEAT('b',64),'history/path',
                    'AVAILABLE','CLEAN',UTC_TIMESTAMP(3),'oem',@firstRecipientId,0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
                SELECT LAST_INSERT_ID();
                """, new { transferId, uploadedById = ids[2], firstRecipientId = ids[3] });
            await connection.ExecuteAsync("""
                INSERT INTO oem_upload_sessions(
                    id,transfer_id,uploader_realm,uploader_id,file_name,file_size,chunk_size,total_chunks,temp_dir,
                    reserved_bytes,status,expires_at,created_at,updated_at)
                VALUES('history-upload',@transferId,'oem',@uploaderId,'upload.pdf',1,1,1,'history/temp',1,'UPLOADING',
                    UTC_TIMESTAMP(3) + INTERVAL 1 DAY,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));

                INSERT INTO oem_download_sessions(
                    id,file_id,file_stored_name,file_sha256,actor_realm,actor_id,login_session_id,purpose,
                    recipient_side,expected_size,status,absolute_deadline,concurrency_version,created_at)
                VALUES('history-download',@fileId,'history-file',REPEAT('b',64),'oem',@downloadActorId,'history-login','DOWNLOAD',
                    1,1,'ACTIVE',UTC_TIMESTAMP(3) + INTERVAL 1 DAY,0,UTC_TIMESTAMP(3));

                INSERT INTO audit_logs(action,target_type,target_id,detail,created_at,actor_realm,actor_account_id)
                VALUES('OEM_HISTORY_TEST','oem_transfer',CAST(@transferId AS CHAR),JSON_OBJECT('source','test'),UTC_TIMESTAMP(3),'oem',@auditActorId);

                INSERT INTO email_outbox(
                    event_type,recipient_email,subject,body,status,retry_count,created_at,recipient_realm,recipient_account_id,oem_transfer_id)
                VALUES('OEM_HISTORY_TEST','recipient@example.invalid','历史','历史','PENDING',0,UTC_TIMESTAMP(3),'oem',@mailRecipientId,@transferId);
                """, new
            {
                transferId,
                fileId,
                uploaderId = ids[4],
                downloadActorId = ids[5],
                auditActorId = ids[6],
                mailRecipientId = ids[7],
            });
        }

        foreach (var accountId in ids)
        {
            var rejected = await admin.DeleteAsync($"/api/v1/oem/accounts/{accountId}", ct).Status(HttpStatusCode.BadRequest);
            Assert.Contains("停用账号", rejected.Json!["message"]!.GetValue<string>());
        }
        var companyRejected = await admin.DeleteAsync($"/api/v1/oem/companies/{companyId}", ct).Status(HttpStatusCode.BadRequest);
        Assert.Contains("关联账号", companyRejected.Json!["message"]!.GetValue<string>());
        var transferCompanyRejected = await admin.DeleteAsync($"/api/v1/oem/companies/{transferOnlyCompanyId}", ct).Status(HttpStatusCode.BadRequest);
        Assert.Contains("传递单", transferCompanyRejected.Json!["message"]!.GetValue<string>());
        Assert.Contains("停用厂商", transferCompanyRejected.Json!["message"]!.GetValue<string>());

        await using var check = await host.OpenAsync(ct);
        Assert.Equal(ids.Length, await check.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM oem_accounts WHERE oem_company_id=@companyId", new { companyId }));
        Assert.Equal(1, await check.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM oem_companies WHERE id=@companyId", new { companyId }));
        Assert.Equal(1, await check.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM oem_companies WHERE id=@transferOnlyCompanyId", new { transferOnlyCompanyId }));
        Assert.Equal(0, await check.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM audit_logs WHERE action='OEM_ACCOUNT_DELETE'"));
        Assert.Equal(0, await check.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM audit_logs WHERE action='OEM_COMPANY_DELETE'"));
    }

    [Fact(Timeout = 180_000)]
    public async Task DeleteRequiresManageAndDeletePermissionsAndRejectsExternalRealms()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var admin = await host.LoginAdminAsync(ct);
        var companyId = (await admin.PostAsync("/api/v1/oem/companies", new { name = "权限删除厂商" }, ct).Ok()).Id();
        var accountId = await CreateAccountAsync(admin, companyId, "permission_vendor", ct, "Vendor#2026");

        await host.CreateInternalUserAsync("manage_only", "Manage#2026x",
            ["oem:company_manage", "oem:account_manage"], null, ct);
        var manageOnly = await host.LoginInternalAsync("manage_only", "Manage#2026x", ct);
        await manageOnly.DeleteAsync($"/api/v1/oem/accounts/{accountId}", ct).Status(HttpStatusCode.Forbidden);
        await manageOnly.DeleteAsync($"/api/v1/oem/companies/{companyId}", ct).Status(HttpStatusCode.Forbidden);

        await host.CreateInternalUserAsync("delete_only", "Delete#2026x",
            ["oem:company_delete", "oem:account_delete"], null, ct);
        var deleteOnly = await host.LoginInternalAsync("delete_only", "Delete#2026x", ct);
        await deleteOnly.DeleteAsync($"/api/v1/oem/accounts/{accountId}", ct).Status(HttpStatusCode.Forbidden);
        await deleteOnly.DeleteAsync($"/api/v1/oem/companies/{companyId}", ct).Status(HttpStatusCode.Forbidden);

        var vendor = await host.LoginOemAsync("permission_vendor", "Vendor#2026", ct);
        await vendor.PutAsync("/api/v1/oem/auth/password", new { oldPassword = "Vendor#2026", newPassword = "Vendor#2027x" }, ct).Ok();
        vendor = await host.LoginOemAsync("permission_vendor", "Vendor#2027x", ct);
        await vendor.DeleteAsync($"/api/v1/oem/accounts/{accountId}", ct).Status(HttpStatusCode.Forbidden);
        await vendor.DeleteAsync($"/api/v1/oem/companies/{companyId}", ct).Status(HttpStatusCode.Forbidden);

        await host.CreateSupplierUserAsync("supplier_delete", "Supplier#2026", ct);
        var supplier = await host.LoginInternalAsync("supplier_delete", "Supplier#2026", ct);
        await supplier.DeleteAsync($"/api/v1/oem/accounts/{accountId}", ct).Status(HttpStatusCode.Forbidden, 40304);
        await supplier.DeleteAsync($"/api/v1/oem/companies/{companyId}", ct).Status(HttpStatusCode.Forbidden, 40304);

        await using var connection = await host.OpenAsync(ct);
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM oem_accounts WHERE id=@accountId", new { accountId }));
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM oem_companies WHERE id=@companyId", new { companyId }));
    }

    private static async Task<ulong> CreateAccountAsync(
        ApiClient admin,
        ulong companyId,
        string employeeNo,
        CancellationToken ct,
        string password = "Vendor#2026")
    {
        var account = await admin.PostAsync($"/api/v1/oem/companies/{companyId}/accounts", new
        {
            employeeNo,
            realName = "删除测试账号" + employeeNo,
            email = employeeNo + "@vendor.invalid",
            password,
        }, ct).Ok();
        return account.Id();
    }
}
