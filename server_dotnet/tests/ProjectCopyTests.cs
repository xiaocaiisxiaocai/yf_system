using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Files;
using Yf.Api.Modules.Projects;

namespace Yf.Api.Tests;

[Collection(ConnectionLifecycleCollectionDefinition.Name)]
public sealed class ProjectCopyTests
{
    [Fact(Timeout = 120_000)]
    public async Task DeletingProjectCancelsPendingMailPreservesHistoryAndPublishesDeletion()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("project_delete_signal", ct);
        await database.InitializeBusinessFixtureAsync(ct);
        await SchemaMigrations.ApplyAsync(database.Database, ct);
        await database.ExecuteAsync(SeedSql + """
            INSERT INTO email_outbox(event_type,project_id,recipient_email,subject,body,status,created_at) VALUES
              ('MESSAGE_CREATED',7102,'pending@example.test','pending','pending','PENDING',UTC_TIMESTAMP()),
              ('MESSAGE_CREATED',7102,'sent@example.test','sent','sent','SENT',UTC_TIMESTAMP());
            """, ct);

        var publisher = new RecordingPublisher();
        var audit = new AuditService([]);
        var projects = new ProjectService(audit, database.Options, new ProjectGroupStatusService(audit), publisher);
        await using var conn = await database.Database.OpenAsync(ct);
        await projects.DeleteAsync(conn, new CurrentUser(1, "admin", "INTERNAL", null), 7102, null, ct);

        Assert.False(await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM projects WHERE id=7102)", cancellationToken: ct)));
        var mail = (await conn.QueryAsync<(string Status, ulong? ProjectId)>(new CommandDefinition(
            "SELECT status AS Status,project_id AS ProjectId FROM email_outbox ORDER BY id",
            cancellationToken: ct))).ToArray();
        Assert.Equal(["CANCELLED", "SENT"], mail.Select(row => row.Status));
        Assert.All(mail, row => Assert.Null(row.ProjectId));
        var signal = Assert.Single(publisher.Calls);
        Assert.Equal((7102UL, RealtimeChangeKinds.Project), signal);
    }

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
            await FileBlobBackfill.RunAsync(database.Database, storage, ct);
            await FileBlobBackfill.RunAsync(database.Database, storage, ct);
            Assert.True(File.Exists(sourcePath));

            var options = new AppOptions { StorageRoot = storage };
            var publisher = new RecordingPublisher();
            var audit = new AuditService([]);
            var groupStatus = new ProjectGroupStatusService(audit);
            var service = new ProjectCopyService(database.Database, options, audit, publisher, groupStatus);
            var actor = new CurrentUser(1, "admin", "INTERNAL", null);
            await using var conn = await database.Database.OpenAsync(ct);
            // Make the database default deliberately non-UTC. The copy path must explicitly use
            // DbClock's UTC value rather than passing only because this server currently runs UTC.
            await conn.ExecuteAsync(new CommandDefinition("""
                ALTER TABLE projects
                MODIFY COLUMN updated_at DATETIME(3) NOT NULL DEFAULT '2000-01-01 00:00:00.000'
                """, cancellationToken: ct));
            var result = Json(await service.CopyAsync(conn, actor, 7101, new() { Name = "复制项目" }, null, ct));
            var targetId = result.RootElement.GetProperty("copy").GetProperty("targetProjectId").GetUInt64();
            var copyId = result.RootElement.GetProperty("copy").GetProperty("copyId").GetUInt64();
            Assert.Equal(1, result.RootElement.GetProperty("copy").GetProperty("fileCount").GetInt32());
            Assert.Equal((ulong)bytes.Length, result.RootElement.GetProperty("copy").GetProperty("totalBytes").GetUInt64());
            Assert.Equal("DRAFT", result.RootElement.GetProperty("project").GetProperty("status").GetString());
            Assert.True(result.RootElement.GetProperty("project").GetProperty("hasCopyHistory").GetBoolean());

            var copied = await conn.QuerySingleAsync<CopiedFile>(new CommandDefinition(
                "SELECT id AS Id,blob_id AS BlobId,storage_path AS StoragePath,uploader_id AS UploaderId,direction AS Direction,sha256 AS Sha256 FROM files WHERE project_id=@TargetId",
                new { TargetId = targetId }, cancellationToken: ct));
            Assert.NotEqual(sourceRelative, copied.StoragePath);
            Assert.Equal(4UL, copied.UploaderId);
            Assert.Equal("S2C", copied.Direction);
            Assert.Equal(sha, copied.Sha256);
            var sourceBlob = await conn.QuerySingleAsync<CopiedFile>(new CommandDefinition(
                "SELECT id AS Id,blob_id AS BlobId,storage_path AS StoragePath,sha256 AS Sha256 FROM files WHERE id=8101",
                cancellationToken: ct));
            Assert.NotEqual(sourceBlob.Id, copied.Id);
            Assert.NotNull(sourceBlob.BlobId);
            Assert.Equal(sourceBlob.BlobId, copied.BlobId);
            Assert.Equal(sourceBlob.StoragePath, copied.StoragePath);
            Assert.Equal(1, await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM file_blobs WHERE sha256=@Sha", new { Sha = sha }, cancellationToken: ct)));
            var timestamps = await conn.QuerySingleAsync<ProjectTimestamps>(new CommandDefinition(
                "SELECT created_at AS CreatedAt,updated_at AS UpdatedAt FROM projects WHERE id=@TargetId",
                new { TargetId = targetId }, cancellationToken: ct));
            Assert.Equal(timestamps.CreatedAt, timestamps.UpdatedAt);
            Assert.NotEqual(new DateTime(2000, 1, 1), timestamps.UpdatedAt);
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
            // The source is protected while its copy exists; deleting the copy itself is covered separately.
            Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() =>
                projects.DeleteAsync(conn, actor, 7101, null, ct))).Status);

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
            var conversionError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                FileBlobBackfill.RunAsync(database.Database, storage, ct));
            Assert.Contains("file 8999", conversionError.Message, StringComparison.Ordinal);
            Assert.Contains("missing", conversionError.Message, StringComparison.OrdinalIgnoreCase);

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
            Assert.True(File.Exists(sourcePath));
            Assert.False(File.Exists(copiedPath));
            await conn.OpenAsync(ct);
            Assert.Equal(2, await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM files WHERE id IN (8101,@CopiedId) AND status='PURGED'",
                new { CopiedId = copied.Id }, cancellationToken: ct)));
            Assert.Equal(2, await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM files WHERE id IN (8101,@CopiedId) AND blob_id IS NULL",
                new { CopiedId = copied.Id }, cancellationToken: ct)));
            Assert.Equal(0, await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM file_blobs WHERE id=@BlobId",
                new { BlobId = copied.BlobId }, cancellationToken: ct)));
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

    [Fact(Timeout = 120_000)]
    public async Task DeletingCopyTargetReleasesCopiedFilesButProtectsSources()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("project_copy_delete", ct);
        await database.InitializeBusinessFixtureAsync(ct);
        await SchemaMigrations.ApplyAsync(database.Database, ct);
        var storage = Path.Combine(Path.GetTempPath(), "yf-project-copy-delete-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storage);
        try
        {
            await database.ExecuteAsync(SeedSql, ct);
            var bytes = "copy-delete-content"u8.ToArray();
            var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var sourceRelative = "files/2026/09/copy-delete.txt";
            var sourcePath = Path.Combine(storage, sourceRelative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            await File.WriteAllBytesAsync(sourcePath, bytes, ct);
            await database.ExecuteAsync($"""
                INSERT INTO files(id,project_id,uploader_id,direction,original_name,stored_name,ext,size_bytes,mime_type,sha256,storage_path,status,created_at)
                VALUES(8101,7101,4,'S2C','源文件.txt','copy-delete.txt','txt',{bytes.Length},'text/plain','{sha}','{sourceRelative}','AVAILABLE',UTC_TIMESTAMP(3));
                """, ct);
            await FileBlobBackfill.RunAsync(database.Database, storage, ct);
            var blobPath = Path.Combine(storage, FileBlobStore.RelativePath(sha).Replace('/', Path.DirectorySeparatorChar));

            var options = new AppOptions { StorageRoot = storage };
            var audit = new AuditService([]);
            var groupStatus = new ProjectGroupStatusService(audit);
            var copies = new ProjectCopyService(database.Database, options, audit, new RecordingPublisher(), groupStatus);
            var projects = new ProjectService(audit, options, groupStatus);
            var actor = new CurrentUser(1, "admin", "INTERNAL", null);
            await using var conn = await database.Database.OpenAsync(ct);
            async Task<ulong> CopyAsync(ulong sourceId, string name)
            {
                using var result = Json(await copies.CopyAsync(conn, actor, sourceId, new() { Name = name }, null, ct));
                return result.RootElement.GetProperty("copy").GetProperty("targetProjectId").GetUInt64();
            }
            Task<long> CountAsync(string sql, object? args = null) =>
                conn.ExecuteScalarAsync<long>(new CommandDefinition(sql, args, cancellationToken: ct));

            var copyId = await CopyAsync(7101, "副本");
            var nestedId = await CopyAsync(copyId, "副本的副本");
            var blobId = await conn.ExecuteScalarAsync<ulong>(new CommandDefinition(
                "SELECT blob_id FROM files WHERE id=8101", cancellationToken: ct));

            // A copy that has itself been copied is a source: it stays until its own copy is gone.
            var nestedSource = await Assert.ThrowsAsync<ApiException>(() => projects.DeleteAsync(conn, actor, copyId, null, ct));
            Assert.Equal(409, nestedSource.Status);
            Assert.Equal("该子项目已被复制出副本，请先删除副本", nestedSource.Message);
            await projects.DeleteAsync(conn, actor, nestedId, null, ct);

            // Anything added after the copy (even a soft-deleted upload or a message) still blocks deletion.
            await conn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO files(id,project_id,uploader_id,direction,original_name,stored_name,ext,size_bytes,mime_type,sha256,storage_path,status,deleted_at,created_at)
                VALUES(98102,@CopyId,1,'C2S','后传.txt','later-upload.txt','txt',1,'text/plain',REPEAT('1',64),'files/2026/09/later-upload.txt','DELETED',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3))
                """, new { CopyId = copyId }, cancellationToken: ct));
            Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() =>
                projects.DeleteAsync(conn, actor, copyId, null, ct))).Status);
            await conn.ExecuteAsync(new CommandDefinition("DELETE FROM files WHERE id=98102", cancellationToken: ct));
            await conn.ExecuteAsync(new CommandDefinition(
                "INSERT INTO messages(id,project_id,sender_id,content,status,created_at) VALUES(98201,@CopyId,1,'副本留言','NORMAL',UTC_TIMESTAMP(3))",
                new { CopyId = copyId }, cancellationToken: ct));
            Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() =>
                projects.DeleteAsync(conn, actor, copyId, null, ct))).Status);
            await conn.ExecuteAsync(new CommandDefinition("DELETE FROM messages WHERE id=98201", cancellationToken: ct));
            // Once started (here: terminated), even the copied files block deletion like any other project.
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE projects SET status='TERMINATED' WHERE id=@CopyId", new { CopyId = copyId }, cancellationToken: ct));
            Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() =>
                projects.DeleteAsync(conn, actor, copyId, null, ct))).Status);
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE projects SET status='DRAFT' WHERE id=@CopyId", new { CopyId = copyId }, cancellationToken: ct));

            await projects.DeleteAsync(conn, actor, copyId, null, ct);
            Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM projects WHERE id IN (@CopyId,@NestedId)", new { CopyId = copyId, NestedId = nestedId }));
            Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM project_copies"));
            Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM file_copy_refs"));
            Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM files"));
            Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM files WHERE id=8101 AND status='AVAILABLE' AND blob_id=@BlobId", new { BlobId = blobId }));
            Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM file_blobs WHERE id=@BlobId AND state='READY'", new { BlobId = blobId }));
            Assert.True(File.Exists(blobPath));
            var details = await conn.ExecuteScalarAsync<string>(new CommandDefinition(
                "SELECT detail FROM audit_logs WHERE action='PROJECT_DELETE' AND target_id=@CopyId",
                new { CopyId = copyId.ToString(System.Globalization.CultureInfo.InvariantCulture) }, cancellationToken: ct));
            using (var detail = JsonDocument.Parse(details!))
            {
                Assert.Equal(7101UL, detail.RootElement.GetProperty("copiedFromProjectId").GetUInt64());
                Assert.Equal(1UL, detail.RootElement.GetProperty("copiedFileCount").GetUInt64());
            }
            // With no copies left, the source falls back to the ordinary rule (it still has its own file).
            Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() =>
                projects.DeleteAsync(conn, actor, 7101, null, ct))).Status);

            // When the copy holds the last reference to the content, deleting it hands the blob to GC.
            var lastCopyId = await CopyAsync(7101, "最后引用副本");
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE files SET status='DELETED',deleted_at=UTC_TIMESTAMP(6)-INTERVAL 31 DAY WHERE id=8101", cancellationToken: ct));
            var maintenance = new FilesMaintenanceService(database.Database, options, NullLogger<FilesMaintenanceService>.Instance);
            // The test pool is small; release this connection while maintenance opens its own.
            await conn.CloseAsync();
            await maintenance.RunGarbageCollectionAsync(ct);
            await conn.OpenAsync(ct);
            Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM files WHERE id=8101 AND status='PURGED' AND blob_id IS NULL"));
            Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM file_blobs WHERE id=@BlobId AND state='READY'", new { BlobId = blobId }));
            await projects.DeleteAsync(conn, actor, lastCopyId, null, ct);
            Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM file_blobs WHERE id=@BlobId AND state='GC_PENDING'", new { BlobId = blobId }));
            Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM files WHERE id=8101 AND status='PURGED'"));
            await conn.CloseAsync();
            await maintenance.RunGarbageCollectionAsync(ct);
            await conn.OpenAsync(ct);
            Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM file_blobs WHERE id=@BlobId", new { BlobId = blobId }));
            Assert.False(File.Exists(blobPath));
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
        VALUES(6203,'PRIORITY','复制优先级',NULL,1,'ACTIVE'),
              (1006203,'ROBOT_TYPE','复制优先级-Robot类型',NULL,1,'ACTIVE');
        INSERT INTO project_groups(id,name,description,supplier_id,status,created_by,machine_model,robot_part_id,responsible_user_id,section_id,priority_id,robot_type_id,expected_completion_date,created_at,updated_at)
        VALUES(7111,'复制主项目一','复制来源',6101,'DRAFT',1,'M1',6202,3,7001,6203,1006203,'2026-12-31',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
              (7112,'复制主项目二','无文件复制来源',6101,'DRAFT',1,'M1',6202,3,7001,6203,1006203,'2026-12-31',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
        INSERT INTO projects(id,project_group_id,name,description,supplier_id,status,created_by,machine_model,robot_part_id,responsible_user_id,section_id,priority_id,robot_type_id,expected_completion_date,created_at,updated_at)
        VALUES(7101,7111,'源项目','复制来源',6101,'DRAFT',1,'M1',6202,3,7001,6203,1006203,'2026-12-31',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
              (7102,7112,'空源项目','无文件复制来源',6101,'DRAFT',1,'M1',6202,3,7001,6203,1006203,'2026-12-31',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
        INSERT INTO project_group_work_orders(project_group_id,work_order_no,sort_no) VALUES(7111,'WO-COPY',0),(7112,'WO-EMPTY',0);
        INSERT INTO project_work_orders(project_id,work_order_no,sort_no) VALUES(7101,'WO-COPY',0),(7102,'WO-EMPTY',0);
        """;

    private static JsonDocument Json(object value) => JsonDocument.Parse(JsonSerializer.Serialize(value, TestJson.Web));
    private sealed class CopiedFile { public ulong Id { get; init; } public ulong? BlobId { get; init; } public string StoragePath { get; init; } = "";
        public ulong UploaderId { get; init; } public string Direction { get; init; } = ""; public string Sha256 { get; init; } = ""; }
    private sealed class ProjectTimestamps
    {
        public DateTime CreatedAt { get; init; }
        public DateTime UpdatedAt { get; init; }
    }
    private sealed class CopiedProject
    {
        public string MachineModel { get; init; } = "";
        public DateTime ExpectedCompletionDate { get; init; }
        public string WorkOrders { get; init; } = "";
    }
    private sealed class RecordingPublisher : IProjectRealtimePublisher
    {
        internal List<(ulong ProjectId, string Kind)> Calls { get; } = [];
        internal IReadOnlyList<ulong> ProjectIds => Calls.Select(call => call.ProjectId).ToArray();
        public Task PublishAsync(ulong projectId, string kind, CancellationToken ct = default)
        { Calls.Add((projectId, kind)); return Task.CompletedTask; }
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
