using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Files;
using Yf.Api.Modules.Projects;

namespace Yf.Api.Tests;

[Collection(ConnectionLifecycleCollection.Name)]
public sealed class ProjectCopyTests
{
    [Fact(Timeout = 120_000)]
    public async Task CompletedSourceCopyUsesCurrentMainDataAndIgnoresRealtimeFailureAfterCommit()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("project_copy_snapshot", ct);
        await database.InitializeBusinessFixtureAsync(ct);
        await SchemaMigrations.ApplyAsync(database.Database, ct);
        await database.ExecuteAsync(SeedSql + """
            UPDATE project_groups
            SET status='IN_PROGRESS',machine_model='主项目当前机型',expected_completion_date='2027-02-01',updated_at=UTC_TIMESTAMP(3)
            WHERE id=7111;
            UPDATE projects
            SET status='COMPLETED',machine_model='已完成快照机型',expected_completion_date='2026-01-01',updated_at=UTC_TIMESTAMP(3)
            WHERE id=7101;
            DELETE FROM project_work_orders WHERE project_id=7101;
            INSERT INTO project_work_orders(project_id,work_order_no,sort_no) VALUES(7101,'WO-FROZEN',0);
            """, ct);
        var storage = Path.Combine(Path.GetTempPath(), "yf-project-copy-snapshot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storage);
        try
        {
            var publisher = new ThrowingPublisher();
            var audit = new AuditService([]);
            var service = new ProjectCopyService(database.Database, new AppOptions { StorageRoot = storage }, audit,
                publisher, new ProjectGroupStatusService(audit));
            await using var conn = await database.Database.OpenAsync(ct);

            using var result = Json(await service.CopyAsync(conn,
                new CurrentUser(1, "admin", "INTERNAL", null), 7101,
                new ProjectCopyRequest { Name = "完成快照复制件" }, null, ct));
            var targetId = result.RootElement.GetProperty("copy").GetProperty("targetProjectId").GetUInt64();
            var target = await conn.QuerySingleAsync<CopiedProject>(new CommandDefinition("""
                SELECT machine_model AS MachineModel,expected_completion_date AS ExpectedCompletionDate,
                       (SELECT GROUP_CONCAT(work_order_no ORDER BY sort_no,id)
                        FROM project_work_orders WHERE project_id=p.id) AS WorkOrders
                FROM projects p WHERE id=@TargetId
                """, new { TargetId = targetId }, cancellationToken: ct));
            Assert.Equal("主项目当前机型", target.MachineModel);
            Assert.Equal(new DateTime(2027, 2, 1), target.ExpectedCompletionDate);
            Assert.Equal("WO-COPY", target.WorkOrders);
            Assert.Equal(2, publisher.ProjectIds.Count);
            Assert.Contains(7101UL, publisher.ProjectIds);
            Assert.Contains(targetId, publisher.ProjectIds);
        }
        finally
        {
            try { Directory.Delete(storage, recursive: true); } catch { }
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task CopyCreatesIndependentFilesAndPermissionFilteredHistory()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("project_copy", ct);
        await database.InitializeBusinessFixtureAsync(ct);
        await SchemaMigrations.ApplyAsync(database.Database, ct);
        var storage = Path.Combine(Path.GetTempPath(), "yf-project-copy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storage);
        try
        {
            await database.ExecuteAsync(SeedSql, ct);
            var bytes = "independent-copy-content"u8.ToArray();
            var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var sourceRelative = "files/2026/09/source-copy.txt";
            var sourcePath = Path.Combine(storage, sourceRelative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            await File.WriteAllBytesAsync(sourcePath, bytes, ct);
            await database.ExecuteAsync($"""
                INSERT INTO files(id,project_id,uploader_id,direction,original_name,stored_name,ext,size_bytes,mime_type,sha256,storage_path,status,created_at)
                VALUES(8101,7101,4,'S2C','源文件.txt','source-copy.txt','txt',{bytes.Length},'text/plain','{sha}','{sourceRelative}','AVAILABLE',UTC_TIMESTAMP(3));
                INSERT INTO messages(id,project_id,sender_id,content,status,created_at)
                VALUES(8201,7101,1,'不应复制的留言','NORMAL',UTC_TIMESTAMP(3));
                """, ct);

            var options = new AppOptions { StorageRoot = storage };
            var publisher = new RecordingPublisher();
            var audit = new AuditService([]);
            var groupStatus = new ProjectGroupStatusService(audit);
            var service = new ProjectCopyService(database.Database, options, audit, publisher, groupStatus);
            var actor = new CurrentUser(1, "admin", "INTERNAL", null);
            await using var conn = await database.Database.OpenAsync(ct);
            var result = Json(await service.CopyAsync(conn, actor, 7101, new() { Name = "复制项目" }, null, ct));
            var targetId = result.RootElement.GetProperty("copy").GetProperty("targetProjectId").GetUInt64();
            var copyId = result.RootElement.GetProperty("copy").GetProperty("copyId").GetUInt64();
            Assert.Equal(1, result.RootElement.GetProperty("copy").GetProperty("fileCount").GetInt32());
            Assert.Equal((ulong)bytes.Length, result.RootElement.GetProperty("copy").GetProperty("totalBytes").GetUInt64());
            Assert.Equal("DRAFT", result.RootElement.GetProperty("project").GetProperty("status").GetString());
            Assert.True(result.RootElement.GetProperty("project").GetProperty("hasCopyHistory").GetBoolean());

            var copied = await conn.QuerySingleAsync<CopiedFile>(new CommandDefinition(
                "SELECT id AS Id,storage_path AS StoragePath,uploader_id AS UploaderId,direction AS Direction,sha256 AS Sha256 FROM files WHERE project_id=@TargetId",
                new { TargetId = targetId }, cancellationToken: ct));
            Assert.NotEqual(sourceRelative, copied.StoragePath);
            Assert.Equal(4UL, copied.UploaderId);
            Assert.Equal("S2C", copied.Direction);
            Assert.Equal(sha, copied.Sha256);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(storage,
                copied.StoragePath.Replace('/', Path.DirectorySeparatorChar)), ct));
            Assert.Equal(0, await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM messages WHERE project_id=@TargetId", new { TargetId = targetId }, cancellationToken: ct)));
            Assert.Equal(["COPY"], (await conn.QueryAsync<string>(new CommandDefinition(
                "SELECT action FROM project_status_logs WHERE project_id=@TargetId", new { TargetId = targetId }, cancellationToken: ct))).ToArray());
            Assert.Equal(2, await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM project_activities WHERE source_key LIKE @Key", new { Key = $"project-copy:{copyId}:%" }, cancellationToken: ct)));

            using (var history = Json(await service.HistoryAsync(conn, actor, 7101, ct)))
                Assert.Equal(targetId, history.RootElement.GetProperty("copies")[0].GetProperty("projectId").GetUInt64());
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE projects SET name='源项目改名' WHERE id=7101; UPDATE projects SET name='复制项目改名' WHERE id=@TargetId",
                new { TargetId = targetId }, cancellationToken: ct));
            using (var snapshotHistory = Json(await service.HistoryAsync(conn, actor, 7101, ct)))
                Assert.Equal("复制项目", snapshotHistory.RootElement.GetProperty("copies")[0].GetProperty("name").GetString());
            using (var mappings = Json(await service.FileHistoryAsync(conn, actor, copyId, 1, 20, ct)))
            {
                Assert.Equal(1, mappings.RootElement.GetProperty("total").GetInt32());
                Assert.Equal(8101UL, mappings.RootElement.GetProperty("list")[0].GetProperty("sourceFileId").GetUInt64());
                Assert.Equal(copied.Id, mappings.RootElement.GetProperty("list")[0].GetProperty("targetFileId").GetUInt64());
            }

            var projects = new ProjectService(audit, options, groupStatus);
            Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() =>
                projects.DeleteAsync(conn, actor, 7101, null, ct))).Status);
            Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() =>
                projects.DeleteAsync(conn, actor, targetId, null, ct))).Status);

            using var emptyCopy = Json(await service.CopyAsync(conn, actor, 7102, new() { Name = "空项目副本" }, null, ct));
            Assert.Equal(0, emptyCopy.RootElement.GetProperty("copy").GetProperty("fileCount").GetInt32());
            Assert.Equal(0UL, emptyCopy.RootElement.GetProperty("copy").GetProperty("totalBytes").GetUInt64());
            Assert.Equal(4, publisher.ProjectIds.Count);
            Assert.Contains(7101UL, publisher.ProjectIds);
            Assert.Contains(7102UL, publisher.ProjectIds);
            Assert.Contains(targetId, publisher.ProjectIds);

            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE projects SET responsible_user_id=2,updated_at=UTC_TIMESTAMP(3) WHERE id=@TargetId",
                new { TargetId = targetId }, cancellationToken: ct));
            var owner = new CurrentUser(1, "admin", "INTERNAL", null);
            // Admin has view-all, so a limited actor is introduced below to prove filtering.
            var limited = new CurrentUser(3, "copy-owner", "INTERNAL", null);
            using (var filtered = Json(await service.HistoryAsync(conn, limited, 7101, ct)))
            {
                Assert.Empty(filtered.RootElement.GetProperty("copies").EnumerateArray());
                Assert.True(filtered.RootElement.GetProperty("hasRestrictedRelations").GetBoolean());
            }
            Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() =>
                service.FileHistoryAsync(conn, limited, copyId, 1, 20, ct))).Status);

            var beforeFiles = Directory.EnumerateFiles(storage, "*", SearchOption.AllDirectories).Count();
            await conn.ExecuteAsync(new CommandDefinition(
                "INSERT INTO files(id,project_id,uploader_id,direction,original_name,stored_name,ext,size_bytes,mime_type,sha256,storage_path,status,created_at) VALUES(8999,7101,1,'C2S','缺失.txt','missing.txt','txt',1,'text/plain',REPEAT('0',64),'files/2026/09/missing.txt','AVAILABLE',UTC_TIMESTAMP(3))",
                cancellationToken: ct));
            Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() =>
                service.CopyAsync(conn, actor, 7101, new() { Name = "复制失败项目" }, null, ct))).Status);
            Assert.False(await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS(SELECT 1 FROM projects WHERE name='复制失败项目')", cancellationToken: ct)));
            Assert.Equal(beforeFiles, Directory.EnumerateFiles(storage, "*", SearchOption.AllDirectories).Count());

            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE project_dictionaries SET status='DISABLED' WHERE id=6203", cancellationToken: ct));
            Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() =>
                service.CopyAsync(conn, actor, 7102, new() { Name = "失效关联复制" }, null, ct))).Status);
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE project_dictionaries SET status='ACTIVE' WHERE id=6203", cancellationToken: ct));

            var copiedPath = Path.Combine(storage, copied.StoragePath.Replace('/', Path.DirectorySeparatorChar));
            await conn.ExecuteAsync(new CommandDefinition("""
                UPDATE files SET status='DELETED',deleted_at=UTC_TIMESTAMP(6)-INTERVAL 31 DAY
                WHERE id IN (8101,@CopiedId)
                """, new { CopiedId = copied.Id }, cancellationToken: ct));
            await conn.CloseAsync();
            var maintenance = new FilesMaintenanceService(database.Database, options,
                NullLogger<FilesMaintenanceService>.Instance);
            await maintenance.RunGarbageCollectionAsync(ct);
            Assert.False(File.Exists(sourcePath));
            Assert.False(File.Exists(copiedPath));
            await conn.OpenAsync(ct);
            Assert.Equal(2, await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM files WHERE id IN (8101,@CopiedId) AND status='PURGED'",
                new { CopiedId = copied.Id }, cancellationToken: ct)));
            await conn.CloseAsync();
            var downloadContext = new DefaultHttpContext();
            downloadContext.Items[typeof(CurrentUser)] = actor;
            var files = new FileService(database.Database, options, audit, new BatchDownloadLimiter(),
                new MediaGrantService(options), new DownloadGrantService(), null!);
            Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() =>
                files.StreamAsync(downloadContext, copied.Id, inline: false, ct))).Status);
            await conn.OpenAsync(ct);
            using (var purgedHistory = Json(await service.FileHistoryAsync(conn, actor, copyId, 1, 20, ct)))
            {
                Assert.True(purgedHistory.RootElement.GetProperty("list")[0].GetProperty("sourceDeleted").GetBoolean());
                Assert.True(purgedHistory.RootElement.GetProperty("list")[0].GetProperty("targetDeleted").GetBoolean());
            }
        }
        finally
        {
            try { Directory.Delete(storage, recursive: true); } catch { }
        }
    }

    private const string SeedSql = """
        INSERT INTO suppliers(id,name,status,created_at,updated_at) VALUES(6101,'复制测试供应商','ACTIVE',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
        INSERT INTO departments(id,parent_id,name,kind,created_at,updated_at) VALUES(7001,NULL,'复制测试课','SECTION',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
        INSERT INTO users(id,employee_no,password_hash,real_name,email,user_type,supplier_id,department_id,status,must_change_password,failed_login_attempts,created_at,updated_at)
        VALUES(1,'admin','unused','系统管理员','','INTERNAL',NULL,7001,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
              (2,'copy-other','unused','其他负责人','','INTERNAL',NULL,7001,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
              (3,'copy-owner','unused','受限负责人','','INTERNAL',NULL,7001,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
              (4,'copy-supplier','unused','源文件上传人','','SUPPLIER',6101,NULL,'ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
        INSERT INTO roles(id,name,is_built_in,status) VALUES(6001,'复制受限角色',0,'ACTIVE');
        INSERT INTO role_permissions(role_id,permission_id) SELECT 6001,id FROM permissions WHERE code IN ('project:list','project:create','project:delete');
        INSERT INTO user_roles(user_id,role_id) VALUES(1,1),(3,6001),(4,4);
        INSERT INTO robot_parts(id,supplier_id,part_number,model,sort_no,status)
        VALUES(6202,6101,'COPY-PART','复制型号',1,'ACTIVE');
        INSERT INTO project_dictionaries(id,type,name,parent_id,sort_no,status)
        VALUES(6203,'PRIORITY','复制优先级',NULL,1,'ACTIVE');
        INSERT INTO project_groups(id,name,description,supplier_id,status,created_by,machine_model,robot_part_id,responsible_user_id,section_id,priority_id,expected_completion_date,created_at,updated_at)
        VALUES(7111,'复制主项目一','复制来源',6101,'DRAFT',1,'M1',6202,3,7001,6203,'2026-12-31',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
              (7112,'复制主项目二','无文件复制来源',6101,'DRAFT',1,'M1',6202,3,7001,6203,'2026-12-31',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
        INSERT INTO projects(id,project_group_id,name,description,supplier_id,status,created_by,machine_model,robot_part_id,responsible_user_id,section_id,priority_id,expected_completion_date,created_at,updated_at)
        VALUES(7101,7111,'源项目','复制来源',6101,'DRAFT',1,'M1',6202,3,7001,6203,'2026-12-31',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
              (7102,7112,'空源项目','无文件复制来源',6101,'DRAFT',1,'M1',6202,3,7001,6203,'2026-12-31',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
        INSERT INTO project_group_work_orders(project_group_id,work_order_no,sort_no) VALUES(7111,'WO-COPY',0),(7112,'WO-EMPTY',0);
        INSERT INTO project_work_orders(project_id,work_order_no,sort_no) VALUES(7101,'WO-COPY',0),(7102,'WO-EMPTY',0);
        """;

    private static JsonDocument Json(object value) => JsonDocument.Parse(JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    private sealed class CopiedFile { public ulong Id { get; init; } public string StoragePath { get; init; } = "";
        public ulong UploaderId { get; init; } public string Direction { get; init; } = ""; public string Sha256 { get; init; } = ""; }
    private sealed class CopiedProject
    {
        public string MachineModel { get; init; } = "";
        public DateTime ExpectedCompletionDate { get; init; }
        public string WorkOrders { get; init; } = "";
    }
    private sealed class RecordingPublisher : IProjectRealtimePublisher
    {
        internal List<ulong> ProjectIds { get; } = [];
        public Task PublishAsync(ulong projectId, string kind, CancellationToken ct = default)
        { ProjectIds.Add(projectId); return Task.CompletedTask; }
    }
    private sealed class ThrowingPublisher : IProjectRealtimePublisher
    {
        internal List<ulong> ProjectIds { get; } = [];
        public Task PublishAsync(ulong projectId, string kind, CancellationToken ct = default)
        {
            ProjectIds.Add(projectId);
            throw new InvalidOperationException("simulated realtime failure");
        }
    }
}
