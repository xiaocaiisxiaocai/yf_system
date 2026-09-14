using System.Text.Json;
using Yf.Api.Infrastructure;
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

        var response = JsonSerializer.SerializeToElement(row.ToResponse(), JsonSerializerOptions.Web);

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

        var response = JsonSerializer.SerializeToElement(row.ToResponse(), JsonSerializerOptions.Web);

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

        var response = JsonSerializer.SerializeToElement(row.ToResponse(), JsonSerializerOptions.Web);

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
}
