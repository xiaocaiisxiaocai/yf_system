using Dapper;
using MySqlConnector;
using Yf.Api.Infrastructure;
using Yf.Api.Infrastructure.Entities;
using Yf.Api.Modules.Projects;

namespace Yf.Api.Tests;

[Collection(ConnectionLifecycleCollectionDefinition.Name)]
public sealed class ProjectActivityAuditContractTests
{
    [Fact]
    public void AuditActionMappingContractIsExplicit()
    {
        var expected = new Dictionary<(string, string), (string Type, string Action, string Title)>
        {
            [("project", "PROJECT_CREATE")] = ("PROJECT", "CREATE", "创建项目"),
            [("project", "PROJECT_UPDATE")] = ("PROJECT", "UPDATE", "编辑项目"),
            [("project", "PROJECT_START")] = ("PROJECT", "START", "开始项目"),
            [("project", "PROJECT_RESTART")] = ("PROJECT", "RESTART", "重新开始项目"),
            [("project", "PROJECT_SUBMIT")] = ("PROJECT", "SUBMIT", "提交项目验收"),
            [("project", "PROJECT_CONFIRM")] = ("PROJECT", "CONFIRM", "确认项目完成"),
            [("project", "PROJECT_REJECT")] = ("PROJECT", "REJECT", "驳回项目验收"),
            [("project", "PROJECT_WITHDRAW")] = ("PROJECT", "WITHDRAW", "撤回项目验收"),
            [("project", "PROJECT_TERMINATE")] = ("PROJECT", "TERMINATE", "终止项目"),
            [("file", "FILE_UPLOAD")] = ("FILE", "UPLOAD", "上传文件"),
            [("file", "FILE_DELETE")] = ("FILE", "DELETE", "删除文件"),
            [("message", "MESSAGE_CREATE")] = ("MESSAGE", "CREATE", "发表留言"),
            [("message", "MESSAGE_DELETE")] = ("MESSAGE", "DELETE", "删除留言"),
        };

        Assert.Equal(expected.Count, ProjectActivityService.AuditActionMappings.Count);
        foreach (var contract in expected)
        {
            var actual = ProjectActivityService.AuditActionMappings[contract.Key];
            Assert.Equal(contract.Value, (actual.ActivityType, actual.Action, actual.Title));
        }
        Assert.Contains(("file", "FILE_PREVIEW"), ProjectActivityService.KnownIgnoredAuditActions);
        Assert.DoesNotContain(("file", "FILE_PREVIEW"), ProjectActivityService.AuditActionMappings.Keys);
    }

    [Fact]
    public async Task UnknownCollaborationAuditActionFailsLoudlyWhilePreviewIsExplicitlyIgnored()
    {
        var service = new ProjectActivityService();
        await using var unopened = new MySqlConnection();
        await service.CaptureAsync(unopened, null, new AuditLog
        {
            Action = "FILE_PREVIEW",
            TargetType = "file",
            TargetId = "1",
        }, null, TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CaptureAsync(
            unopened, null, new AuditLog
            {
                Action = "FILE_NEW_UNMAPPED_ACTION",
                TargetType = "file",
                TargetId = "1",
            }, null, TestContext.Current.CancellationToken));
        Assert.Contains("no ProjectActivity mapping", error.Message, StringComparison.Ordinal);
    }

    [Fact(Timeout = 120_000)]
    public async Task BatchAuditCreatesEveryMappedActivityAndSkipsKnownPreviewAudit()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await MigratedTestDatabase.CreateOrSkipAsync(ct);
        await database.ExecuteAsync("""
            UPDATE users SET must_change_password=0 WHERE id=1;
            INSERT INTO suppliers(id,name,status,created_by) VALUES(9101,'动态合同供应商','ACTIVE',1);
            INSERT INTO project_groups(id,name,supplier_id,status,created_by,responsible_user_id)
            VALUES(9102,'动态合同主项目',9101,'IN_PROGRESS',1,1);
            INSERT INTO projects(id,project_group_id,name,supplier_id,status,created_by,responsible_user_id)
            VALUES(9103,9102,'动态合同项目',9101,'IN_PROGRESS',1,1);
            INSERT INTO files(id,project_id,uploader_id,direction,original_name,stored_name,ext,size_bytes,
                              storage_path,status,created_at,deleted_at)
            VALUES(9104,9103,1,'C2S','合同.txt','contract-activity.txt','txt',1,
                   'files/contract-activity.txt','DELETED',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
            INSERT INTO messages(id,project_id,sender_id,content,status,created_at,deleted_at)
            VALUES(9105,9103,1,'合同留言','DELETED',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
            """, null, ct);

        var writes = ProjectActivityService.AuditActionMappings.Keys.Select(key => new AuditWrite(
            key.AuditAction,
            key.TargetType,
            key.TargetType switch { "project" => 9103UL, "file" => 9104UL, _ => 9105UL },
            key.AuditAction.StartsWith("PROJECT_", StringComparison.Ordinal)
                ? new { reason = "合同原因" }
                : null)).Append(new AuditWrite("FILE_PREVIEW", "file", 9104, null)).ToArray();
        var audit = new AuditService([new ProjectActivityService()]);
        await using var connection = await database.Database.OpenAsync(ct);
        await using (var tx = await AppDb.BeginTransactionAsync(connection, ct))
        {
            await audit.WriteBatchAsync(connection, tx, 1, writes, null, ct);
            await tx.CommitAsync(ct);
        }

        var activities = (await connection.QueryAsync<(string Type, string Action)>(
            "SELECT activity_type AS Type,action AS Action FROM project_activities WHERE project_id=9103"))
            .ToArray();
        Assert.Equal(ProjectActivityService.AuditActionMappings.Count, activities.Length);
        foreach (var mapping in ProjectActivityService.AuditActionMappings.Values)
            Assert.Contains((mapping.ActivityType, mapping.Action), activities);
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM audit_logs WHERE action='FILE_PREVIEW' AND target_id='9104'"));
    }
}
