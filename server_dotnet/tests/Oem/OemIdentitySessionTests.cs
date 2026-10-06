using System.Net;
using Dapper;

namespace Yf.Api.Tests.Oem;

public sealed class OemIdentitySessionTests
{
    [Fact(Timeout = 180_000)]
    public async Task ConcurrentLoginsEnforceTheSharedPerAccountSessionCap()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct, args =>
        {
            args.Add("--App:MaxActiveSessionsPerUser=2");
            args.Add("--App:AbsoluteSessionLifetimeDays=30");
        });
        var admin = await host.LoginAdminAsync(ct);
        var companyId = (await admin.PostAsync("/api/v1/oem/companies", new { name = "并发会话厂商" }, ct).Ok()).Id();
        var accountId = (await admin.PostAsync($"/api/v1/oem/companies/{companyId}/accounts", new
        {
            employeeNo = "session_vendor",
            realName = "会话厂商",
            email = "session-vendor@example.invalid",
            password = "Vendor#2026",
        }, ct).Ok()).Id();

        // Create the clients before starting the requests because the host tracks clients
        // in a non-concurrent collection. The account row lock must serialize the logins.
        var clients = Enumerable.Range(0, 4).Select(_ => host.Anonymous()).ToArray();
        await Task.WhenAll(clients.Select(async client =>
        {
            var login = await client.PostAsync("/api/v1/oem/auth/login",
                new { employeeNo = "session_vendor", password = "Vendor#2026" }, ct).Ok();
            client.Bearer(login["accessToken"]!.GetValue<string>());
        }));

        await using var connection = await host.OpenAsync(ct);
        var activeFamilies = await connection.ExecuteScalarAsync<int>("""
            SELECT COUNT(DISTINCT session_id)
            FROM oem_refresh_tokens
            WHERE account_id=@accountId AND revoked=0
              AND expires_at>UTC_TIMESTAMP(3) AND session_expires_at>UTC_TIMESTAMP(3)
            """, new { accountId });
        Assert.Equal(2, activeFamilies);

        var results = await Task.WhenAll(clients.Select(client => client.GetAsync("/api/v1/oem/auth/me", ct)));
        Assert.Equal(2, results.Count(result => result.Status == HttpStatusCode.OK));
        Assert.Equal(2, results.Count(result => result.Status == HttpStatusCode.Unauthorized));
    }

    [Fact(Timeout = 180_000)]
    public async Task RefreshInheritsAndCannotOutliveTheSessionFamilyDeadline()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct, args =>
        {
            args.Add("--App:RefreshTtlDays=7");
            args.Add("--App:AbsoluteSessionLifetimeDays=30");
        });
        var admin = await host.LoginAdminAsync(ct);
        var companyId = (await admin.PostAsync("/api/v1/oem/companies", new { name = "绝对期限厂商" }, ct).Ok()).Id();
        var accountId = (await admin.PostAsync($"/api/v1/oem/companies/{companyId}/accounts", new
        {
            employeeNo = "deadline_vendor",
            realName = "期限厂商",
            email = "deadline-vendor@example.invalid",
            password = "Vendor#2026",
        }, ct).Ok()).Id();
        var vendor = await host.LoginOemAsync("deadline_vendor", "Vendor#2026", ct);

        await using var connection = await host.OpenAsync(ct);
        await connection.ExecuteAsync("""
            UPDATE oem_refresh_tokens
            SET session_expires_at=DATE_ADD(UTC_TIMESTAMP(3), INTERVAL 10 MINUTE),
                expires_at=DATE_ADD(UTC_TIMESTAMP(3), INTERVAL 10 MINUTE)
            WHERE account_id=@accountId AND revoked=0
            """, new { accountId });
        var family = await connection.QuerySingleAsync<RefreshState>("""
            SELECT session_id SessionId,session_expires_at SessionExpiresAt
            FROM oem_refresh_tokens WHERE account_id=@accountId AND revoked=0
            """, new { accountId });

        await vendor.PostAsync("/api/v1/oem/auth/refresh", null, ct).Ok();
        var rotated = await connection.QuerySingleAsync<RefreshState>("""
            SELECT session_id SessionId,session_expires_at SessionExpiresAt,expires_at ExpiresAt
            FROM oem_refresh_tokens WHERE account_id=@accountId AND revoked=0
            """, new { accountId });
        Assert.Equal(family.SessionId, rotated.SessionId);
        Assert.Equal(family.SessionExpiresAt, rotated.SessionExpiresAt);
        Assert.True(rotated.ExpiresAt <= rotated.SessionExpiresAt);

        // A still-unexpired token cannot revive a family whose absolute deadline passed.
        await connection.ExecuteAsync("""
            UPDATE oem_refresh_tokens
            SET session_expires_at=DATE_SUB(UTC_TIMESTAMP(3), INTERVAL 1 SECOND),
                expires_at=DATE_ADD(UTC_TIMESTAMP(3), INTERVAL 1 DAY)
            WHERE account_id=@accountId AND revoked=0
            """, new { accountId });
        await vendor.PostAsync("/api/v1/oem/auth/refresh", null, ct).Status(HttpStatusCode.Unauthorized);
        await vendor.GetAsync("/api/v1/oem/auth/me", ct).Status(HttpStatusCode.Unauthorized);
    }

    [Fact(Timeout = 180_000)]
    public async Task InternalRecipientWithOnlyOemMailHistoryCannotBeDeleted()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await OemTestHost.StartAsync(ct);
        var admin = await host.LoginAdminAsync(ct);
        var userId = await host.CreateInternalUserAsync(
            "mail_history_user", "MailUser#2026", ["dashboard"], null, ct);
        await using var connection = await host.OpenAsync(ct);
        await connection.ExecuteAsync("""
            INSERT INTO email_outbox(
                event_type,recipient_email,subject,body,status,retry_count,
                recipient_realm,recipient_account_id)
            VALUES('OEM_APPROVAL_PENDING','mail-history@example.invalid','OEM历史','历史','SENT',0,'internal',@userId)
            """, new { userId });

        await admin.DeleteAsync($"/api/v1/admin/users/{userId}", ct).Status(HttpStatusCode.BadRequest);

        Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM users WHERE id=@userId", new { userId }));
    }

    private sealed class RefreshState
    {
        public string SessionId { get; init; } = "";
        public DateTime SessionExpiresAt { get; init; }
        public DateTime ExpiresAt { get; init; }
    }
}
