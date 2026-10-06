using Dapper;
using Microsoft.AspNetCore.Http;
using MySqlConnector;
using System.Text.Json;
using Yf.Api.Infrastructure;
using Yf.Api.Infrastructure.Entities;
using Yf.Api.Modules.SystemManagement;

namespace Yf.Api.Tests;

public class AuditLogTests
{
    [Fact]
    public void SnapshotNamesTakePriorityAndLegacyDetailFieldsRemain()
    {
        var row = new AuditRow
        {
            CurrentActorName = "当前操作人",
            CurrentTargetName = "当前对象名",
            Detail = """
                {
                  "oldName": "旧角色名",
                  "descriptionChanged": true,
                  "auditContext": {
                    "actorName": "操作时姓名",
                    "targetName": "操作时对象名",
                    "requestId": "request-1",
                    "source": "HTTP"
                  }
                }
                """
        };

        var response = JsonSerializer.SerializeToElement(row.ToResponse(), TestJson.Web);

        Assert.Equal("操作时姓名", response.GetProperty("actorName").GetString());
        Assert.Equal("操作时对象名", response.GetProperty("targetName").GetString());
        Assert.Equal("snapshot", response.GetProperty("actorNameSource").GetString());
        Assert.Equal("snapshot", response.GetProperty("targetNameSource").GetString());
        var detail = response.GetProperty("detail");
        Assert.Equal("旧角色名", detail.GetProperty("oldName").GetString());
        Assert.True(detail.GetProperty("descriptionChanged").GetBoolean());
        Assert.Equal("request-1", detail.GetProperty("auditContext").GetProperty("requestId").GetString());
    }

    [Fact]
    public void LegacyRowsUseCurrentNamesAndMarkTheirSource()
    {
        var row = new AuditRow
        {
            CurrentActorName = "当前操作人",
            CurrentTargetName = "当前对象名",
            Detail = "{\"reason\":\"旧格式日志\"}"
        };

        var response = JsonSerializer.SerializeToElement(row.ToResponse(), TestJson.Web);

        Assert.Equal("当前操作人", response.GetProperty("actorName").GetString());
        Assert.Equal("当前对象名", response.GetProperty("targetName").GetString());
        Assert.Equal("current", response.GetProperty("actorNameSource").GetString());
        Assert.Equal("current", response.GetProperty("targetNameSource").GetString());
        Assert.Equal("旧格式日志", response.GetProperty("detail").GetProperty("reason").GetString());
    }

    [Fact]
    public void MissingNamesAreMarkedUnknown()
    {
        var row = new AuditRow { Detail = "{\"auditContext\":{\"actorName\":\"  \",\"targetName\":null}}" };

        var response = JsonSerializer.SerializeToElement(row.ToResponse(), TestJson.Web);

        Assert.Equal(JsonValueKind.Null, response.GetProperty("actorName").ValueKind);
        Assert.Equal(JsonValueKind.Null, response.GetProperty("targetName").ValueKind);
        Assert.Equal("unknown", response.GetProperty("actorNameSource").GetString());
        Assert.Equal("unknown", response.GetProperty("targetNameSource").GetString());
    }

    [Fact]
    public void OnlyChangedDropsJsonEquivalentValuesAndKeepsRealChanges()
    {
        var changes = AuditChange.OnlyChanged(
            new("name", "名称", "相同", "相同"),
            new("member", "成员", new { id = 7UL, name = "同一人" }, new { id = 7UL, name = "同一人" }),
            new("description", "说明", null, "新增说明"),
            new("status", "状态", "ACTIVE", "DISABLED"));

        Assert.Collection(changes,
            change =>
            {
                Assert.Equal("description", change.Field);
                Assert.Null(change.Before);
                Assert.Equal("新增说明", change.After);
            },
            change =>
            {
                Assert.Equal("status", change.Field);
                Assert.Equal("ACTIVE", change.Before);
                Assert.Equal("DISABLED", change.After);
            });
    }

    [Fact(Timeout = 120_000)]
    public async Task RealmActorIsStoredOutsideUsersIdentityAndOemAuditSkipsProjectCapture()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await MigratedTestDatabase.CreateOrSkipAsync(ct);
        var capture = new RecordingCapture();
        var audit = new AuditService([capture]);
        await using var connection = await database.Database.OpenAsync(ct);

        var id = await audit.WriteAsync(
            connection,
            null,
            null,
            "OEM_ACCOUNT_UPDATE",
            "oem_account",
            77,
            new { targetName = "外部账号" },
            null,
            ct,
            realmActor: new AuditRealmActor("oem", 41, "vendor_41", "厂商用户"));

        var row = await connection.QuerySingleAsync<RealmAuditRow>(new CommandDefinition("""
            SELECT user_id AS UserId,actor_realm AS ActorRealm,actor_account_id AS ActorAccountId,
                   employee_no AS EmployeeNo,detail AS Detail
            FROM audit_logs WHERE id=@id
            """, new { id }, cancellationToken: ct));
        Assert.Null(row.UserId);
        Assert.Equal("oem", row.ActorRealm);
        Assert.Equal(41UL, row.ActorAccountId);
        Assert.Equal("vendor_41", row.EmployeeNo);
        Assert.Contains("厂商用户", row.Detail);
        Assert.Empty(capture.Seen);

        await audit.WriteAsync(connection, null, null, "EMAIL_SENT", "email_outbox", 9,
            new { status = "SENT" }, null, ct);
        Assert.Single(capture.Seen);

        var oemRequest = new DefaultHttpContext().Request;
        oemRequest.QueryString = new QueryString("?action=OEM_ACCOUNT_UPDATE");
        var collaborationView = await new SystemService(database.Database, audit).ListLogsAsync(oemRequest, ct);
        Assert.Empty(collaborationView.List);
        Assert.Equal(0UL, collaborationView.Total);
    }

    [Fact]
    public async Task RealmActorCannotAlsoUseAUsersTableId()
    {
        var audit = new AuditService([]);
        await using var unopened = new MySqlConnection();
        await Assert.ThrowsAsync<ArgumentException>(() => audit.WriteAsync(
            unopened,
            null,
            1,
            "OEM_TEST",
            null,
            null,
            null,
            null,
            TestContext.Current.CancellationToken,
            realmActor: new AuditRealmActor("oem", 1, "external", null)));
    }

    private sealed class RecordingCapture : IProjectAuditCapture
    {
        public List<AuditLog> Seen { get; } = [];

        public Task CaptureAsync(
            MySqlConnection db, MySqlTransaction? tx, AuditLog audit, string? actorName, CancellationToken ct)
        {
            Seen.Add(audit);
            return Task.CompletedTask;
        }
    }

    private sealed record RealmAuditRow(
        ulong? UserId,
        string ActorRealm,
        ulong ActorAccountId,
        string EmployeeNo,
        string Detail);
}
