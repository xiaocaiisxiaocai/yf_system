using System.Security.Cryptography;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Projects;

namespace Yf.Api.Tests;

[Collection(ConnectionLifecycleCollection.Name)]
public sealed class BackgroundProjectCopyTests
{
    [Fact(Timeout = 120_000)]
    public async Task DurableJobIsIdempotentAndProducesIndependentVerifiedCopy()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await MigratedTestDatabase.CreateOrSkipAsync(ct);
        var storage = NewStorage(database.Options);
        try
        {
            var source = await SeedAsync(database, storage, includeFile: true, ct);
            await using var provider = Services(database).BuildServiceProvider();
            var service = provider.GetRequiredService<ProjectCopyService>();
            var actor = new CurrentUser(source.ActorId, "admin", "INTERNAL", null);
            await using var conn = await database.Database.OpenAsync(ct);
            var request = new ProjectCopyRequest { Name = "后台复制件", IdempotencyKey = "copy-test-001" };
            var accepted = await service.EnqueueAsync(conn, actor, source.ProjectId, request, "127.0.0.1", ct);
            var repeated = await service.EnqueueAsync(conn, actor, source.ProjectId, request, "127.0.0.1", ct);
            Assert.Equal(accepted.JobId, repeated.JobId);
            Assert.Equal("pending", accepted.Status);
            var mismatch = await Assert.ThrowsAsync<ApiException>(() => service.EnqueueAsync(conn, actor,
                source.ProjectId, new ProjectCopyRequest { Name = "不同请求", IdempotencyKey = "copy-test-001" }, null, ct));
            Assert.Equal(409, mismatch.Status);

            var worker = Worker(database, provider);
            Assert.True(await worker.RunNextAsync(ct));
            Assert.False(await worker.RunNextAsync(ct));
            var finished = await service.GetJobAsync(conn, actor, accepted.JobId, ct);
            Assert.Equal("succeeded", finished.Status);
            Assert.Equal(finished.FilesTotal, finished.FilesCopied);
            Assert.Equal(finished.BytesTotal, finished.BytesCopied);
            Assert.NotNull(finished.Result);
            Assert.Null(finished.Error);
            var listed = await service.ListJobsAsync(conn, actor, source.GroupId, ct);
            Assert.Contains(listed.Jobs, item => item.JobId == accepted.JobId && item.Status == "succeeded");
            const ulong otherAdminId = 99201;
            await conn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO users(id,employee_no,password_hash,real_name,email,user_type,status,must_change_password,failed_login_attempts,created_at,updated_at)
                VALUES(@Id,'other-copy-admin','unused','其他管理员','','INTERNAL','ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
                INSERT INTO user_roles(user_id,role_id)
                SELECT @Id,id FROM roles WHERE name='系统管理员' AND is_built_in=1;
                """, new { Id = otherAdminId }, cancellationToken: ct));
            var otherAdmin = new CurrentUser(otherAdminId, "other-copy-admin", "INTERNAL", null);
            Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() =>
                service.GetJobAsync(conn, otherAdmin, accepted.JobId, ct))).Status);
            Assert.Empty((await service.ListJobsAsync(conn, otherAdmin, source.GroupId, ct)).Jobs);

            var copied = await conn.QuerySingleAsync<CopiedFile>(new CommandDefinition(
                "SELECT storage_path AS StoragePath,sha256 AS Sha256 FROM files WHERE project_id=@ProjectId",
                new { ProjectId = finished.Result!.ProjectId }, cancellationToken: ct));
            Assert.NotEqual(source.StoragePath, copied.StoragePath);
            Assert.Equal(source.Sha256, copied.Sha256);
            Assert.Equal(source.Bytes, await File.ReadAllBytesAsync(Path.Combine(storage,
                copied.StoragePath.Replace('/', Path.DirectorySeparatorChar)), ct));
            Assert.Equal(1, await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM project_copies WHERE source_project_id=@ProjectId",
                new { ProjectId = source.ProjectId }, cancellationToken: ct)));
        }
        finally { TryDeleteStorage(storage); }
    }

    [Fact(Timeout = 120_000)]
    public async Task InterruptedJobReturnsToPendingAndOwnedStagingIsCleanedBeforeRetry()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await MigratedTestDatabase.CreateOrSkipAsync(ct);
        var storage = NewStorage(database.Options);
        try
        {
            var source = await SeedAsync(database, storage, includeFile: true, ct);
            await using var provider = Services(database).BuildServiceProvider();
            var service = provider.GetRequiredService<ProjectCopyService>();
            var actor = new CurrentUser(source.ActorId, "admin", "INTERNAL", null);
            await using var conn = await database.Database.OpenAsync(ct);
            var accepted = await service.EnqueueAsync(conn, actor, source.ProjectId,
                new ProjectCopyRequest { Name = "恢复复制件", IdempotencyKey = "copy-recover-001" }, null, ct);
            var interruptedToken = Guid.NewGuid().ToString("N");
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE project_copy_jobs SET status='running',execution_token=@Token,started_at=UTC_TIMESTAMP(6) WHERE id=@Id",
                new { Id = accepted.JobId, Token = interruptedToken }, cancellationToken: ct));
            var owned = Path.Combine(storage, "copy-jobs", accepted.JobId.ToString(), interruptedToken);
            Directory.CreateDirectory(owned);
            await File.WriteAllTextAsync(Path.Combine(owned, "partial.tmp"), "partial", ct);

            var worker = Worker(database, provider);
            Assert.Null(await service.CheckJobCommitOutcomeAsync(accepted.JobId, ulong.MaxValue, ulong.MaxValue));
            Assert.Equal(1, await worker.RecoverInterruptedJobsAsync(ct));
            Assert.False(Directory.Exists(owned));
            Assert.Equal("pending", (await service.GetJobAsync(conn, actor, accepted.JobId, ct)).Status);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.ExecuteJobAsync(accepted.JobId, interruptedToken, 0, ct));
            Assert.Equal(0, await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM projects WHERE name='恢复复制件'", cancellationToken: ct)));
            Assert.True(await worker.RunNextAsync(ct));
            var succeeded = await service.GetJobAsync(conn, actor, accepted.JobId, ct);
            Assert.Equal("succeeded", succeeded.Status);
            Assert.Equal(1, await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM projects WHERE name='恢复复制件'", cancellationToken: ct)));
            var storedPath = Assert.IsType<string>(await conn.ExecuteScalarAsync<string>(new CommandDefinition(
                "SELECT storage_path FROM files WHERE project_id=@ProjectId",
                new { ProjectId = succeeded.Result!.ProjectId }, cancellationToken: ct)));
            var resultDirectory = Path.GetDirectoryName(Path.Combine(storage,
                storedPath.Replace('/', Path.DirectorySeparatorChar)))!;
            Assert.Equal(0, await worker.ScavengeOwnedDirectoriesAsync(ct));
            Assert.True(Directory.Exists(resultDirectory));
        }
        finally { TryDeleteStorage(storage); }
    }

    [Fact(Timeout = 120_000)]
    public async Task ExecutionFailureIsSafeAndFailedJobDoesNotPreventDeletingSource()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await MigratedTestDatabase.CreateOrSkipAsync(ct);
        var storage = NewStorage(database.Options);
        try
        {
            var source = await SeedAsync(database, storage, includeFile: false, ct);
            await using var provider = Services(database).BuildServiceProvider();
            var service = provider.GetRequiredService<ProjectCopyService>();
            var actor = new CurrentUser(source.ActorId, "admin", "INTERNAL", null);
            await using var conn = await database.Database.OpenAsync(ct);
            var accepted = await service.EnqueueAsync(conn, actor, source.ProjectId,
                new ProjectCopyRequest { Name = "会失败的复制件", IdempotencyKey = "copy-fail-001" }, null, ct);
            var projects = new ProjectService(new AuditService([]), database.Options,
                new ProjectGroupStatusService(new AuditService([])));
            Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() =>
                projects.DeleteAsync(conn, actor, source.ProjectId, null, ct))).Status);

            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE project_dictionaries SET status='DISABLED' WHERE id=@PriorityId",
                new { source.PriorityId }, cancellationToken: ct));
            Assert.True(await Worker(database, provider).RunNextAsync(ct));
            var failed = await service.GetJobAsync(conn, actor, accepted.JobId, ct);
            Assert.Equal("failed", failed.Status);
            Assert.NotNull(failed.Error);
            Assert.DoesNotContain(storage, failed.Error!, StringComparison.OrdinalIgnoreCase);
            Assert.Null(failed.Result);
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE project_dictionaries SET status='ACTIVE' WHERE id=@PriorityId",
                new { source.PriorityId }, cancellationToken: ct));
            await projects.DeleteAsync(conn, actor, source.ProjectId, null, ct);
            Assert.Equal(0, await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM project_copy_jobs WHERE id=@Id", new { Id = accepted.JobId }, cancellationToken: ct)));
        }
        finally { TryDeleteStorage(storage); }
    }

    [Fact(Timeout = 120_000)]
    public async Task DisabledCopyWorkerRejectsDurableSubmission()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await MigratedTestDatabase.CreateOrSkipAsync(ct);
        var storage = NewStorage(database.Options);
        try
        {
            var source = await SeedAsync(database, storage, includeFile: false, ct);
            database.Options.CopyWorkerEnabled = false;
            await using var provider = Services(database).BuildServiceProvider();
            var service = provider.GetRequiredService<ProjectCopyService>();
            await using var conn = await database.Database.OpenAsync(ct);
            var error = await Assert.ThrowsAsync<ApiException>(() => service.EnqueueAsync(conn,
                new CurrentUser(source.ActorId, "admin", "INTERNAL", null), source.ProjectId,
                new ProjectCopyRequest { Name = "不可入队", IdempotencyKey = "worker-disabled-001" }, null, ct));
            Assert.Equal(503, error.Status);
            Assert.Equal(0, await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM project_copy_jobs", cancellationToken: ct)));
        }
        finally { TryDeleteStorage(storage); }
    }

    [Fact(Timeout = 120_000)]
    public async Task WorkerRechecksDisabledRequesterBeforeCopying()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await MigratedTestDatabase.CreateOrSkipAsync(ct);
        var storage = NewStorage(database.Options);
        try
        {
            var source = await SeedAsync(database, storage, includeFile: false, ct);
            await using var provider = Services(database).BuildServiceProvider();
            var service = provider.GetRequiredService<ProjectCopyService>();
            var actor = new CurrentUser(source.ActorId, "admin", "INTERNAL", null);
            await using var conn = await database.Database.OpenAsync(ct);
            var accepted = await service.EnqueueAsync(conn, actor, source.ProjectId,
                new ProjectCopyRequest { Name = "禁用账号不得复制", IdempotencyKey = "disabled-requester-001" }, null, ct);
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE users SET status='DISABLED' WHERE id=@Id", new { Id = source.ActorId }, cancellationToken: ct));
            Assert.True(await Worker(database, provider).RunNextAsync(ct));
            var state = await conn.QuerySingleAsync<JobState>(new CommandDefinition(
                "SELECT status AS Status,error AS Error FROM project_copy_jobs WHERE id=@Id",
                new { Id = accepted.JobId }, cancellationToken: ct));
            Assert.Equal("failed", state.Status);
            Assert.NotNull(state.Error);
            Assert.Equal(0, await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM projects WHERE name='禁用账号不得复制'", cancellationToken: ct)));
        }
        finally { TryDeleteStorage(storage); }
    }

    [Fact(Timeout = 120_000)]
    public async Task JobReadIsDeniedAfterRequesterLosesSourceVisibility()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await MigratedTestDatabase.CreateOrSkipAsync(ct);
        var storage = NewStorage(database.Options);
        try
        {
            var source = await SeedAsync(database, storage, includeFile: false, ct);
            await using var provider = Services(database).BuildServiceProvider();
            var service = provider.GetRequiredService<ProjectCopyService>();
            await using var conn = await database.Database.OpenAsync(ct);
            const ulong limitedId = 99211;
            const ulong limitedRoleId = 99212;
            await conn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO users(id,employee_no,password_hash,real_name,email,user_type,status,must_change_password,failed_login_attempts,created_at,updated_at)
                VALUES(@UserId,'limited-copy-owner','unused','受限复制人','','INTERNAL','ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
                INSERT INTO roles(id,name,is_built_in,status) VALUES(@RoleId,'受限复制角色',0,'ACTIVE');
                INSERT INTO role_permissions(role_id,permission_id)
                SELECT @RoleId,id FROM permissions WHERE code IN ('project:list','project:create');
                INSERT INTO user_roles(user_id,role_id) VALUES(@UserId,@RoleId);
                UPDATE project_groups SET responsible_user_id=@UserId,updated_at=UTC_TIMESTAMP(3) WHERE id=@GroupId;
                UPDATE projects SET responsible_user_id=@UserId,updated_at=UTC_TIMESTAMP(3) WHERE id=@ProjectId;
                """, new { UserId = limitedId, RoleId = limitedRoleId, source.GroupId, source.ProjectId }, cancellationToken: ct));
            var limited = new CurrentUser(limitedId, "limited-copy-owner", "INTERNAL", null);
            var accepted = await service.EnqueueAsync(conn, limited, source.ProjectId,
                new ProjectCopyRequest { Name = "失去可见性任务", IdempotencyKey = "visibility-loss-001" }, null, ct);
            await conn.ExecuteAsync(new CommandDefinition("""
                UPDATE project_groups SET responsible_user_id=@AdminId,updated_at=UTC_TIMESTAMP(3) WHERE id=@GroupId;
                UPDATE projects SET responsible_user_id=@AdminId,updated_at=UTC_TIMESTAMP(3) WHERE id=@ProjectId;
                """, new { AdminId = source.ActorId, source.GroupId, source.ProjectId }, cancellationToken: ct));
            Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() =>
                service.GetJobAsync(conn, limited, accepted.JobId, ct))).Status);
            Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() =>
                service.ListJobsAsync(conn, limited, source.GroupId, ct))).Status);
        }
        finally { TryDeleteStorage(storage); }
    }

    [Fact(Timeout = 120_000)]
    public async Task SupersededWorkerCannotTurnRecoverableJobIntoFailure()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await MigratedTestDatabase.CreateOrSkipAsync(ct);
        var storage = NewStorage(database.Options);
        try
        {
            var source = await SeedAsync(database, storage, includeFile: false, ct);
            await using var provider = Services(database).BuildServiceProvider();
            var service = provider.GetRequiredService<ProjectCopyService>();
            await using var conn = await database.Database.OpenAsync(ct);
            var accepted = await service.EnqueueAsync(conn,
                new CurrentUser(source.ActorId, "admin", "INTERNAL", null), source.ProjectId,
                new ProjectCopyRequest { Name = "租约切换可恢复", IdempotencyKey = "lease-handoff-001" }, null, ct);
            var staleToken = Guid.NewGuid().ToString("N");
            await conn.ExecuteAsync(new CommandDefinition("""
                UPDATE project_copy_jobs
                SET status='running',execution_token=@Token,worker_epoch=1,started_at=UTC_TIMESTAMP(6)
                WHERE id=@JobId;
                UPDATE project_copy_worker_state SET epoch=2 WHERE id=1;
                """, new { Token = staleToken, JobId = accepted.JobId }, cancellationToken: ct));

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.ExecuteJobAsync(accepted.JobId, staleToken, 1, ct));
            var afterStaleExecution = await conn.QuerySingleAsync<JobState>(new CommandDefinition(
                "SELECT status AS Status,error AS Error FROM project_copy_jobs WHERE id=@Id",
                new { Id = accepted.JobId }, cancellationToken: ct));
            Assert.Equal("running", afterStaleExecution.Status);
            Assert.Null(afterStaleExecution.Error);
            Assert.Equal(0, await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM projects WHERE name='租约切换可恢复'", cancellationToken: ct)));

            var worker = Worker(database, provider);
            Assert.Equal(1, await worker.RecoverInterruptedJobsAsync(ct));
            Assert.Equal("pending", (await service.GetJobAsync(conn,
                new CurrentUser(source.ActorId, "admin", "INTERNAL", null), accepted.JobId, ct)).Status);
        }
        finally { TryDeleteStorage(storage); }
    }

    private static IServiceCollection Services(MigratedTestDatabase database)
    {
        var audit = new AuditService([]);
        return new ServiceCollection()
            .AddSingleton(database.Database)
            .AddSingleton(database.Options)
            .AddSingleton(audit)
            .AddSingleton<IProjectRealtimePublisher, NoOpPublisher>()
            .AddScoped(_ => new ProjectGroupStatusService(audit))
            .AddScoped<ProjectCopyService>();
    }

    private static ProjectCopyWorker Worker(MigratedTestDatabase database, IServiceProvider provider) => new(
        database.Database, database.Options, provider.GetRequiredService<IServiceScopeFactory>(),
        NullLogger<ProjectCopyWorker>.Instance);

    private static string NewStorage(AppOptions options)
    {
        var storage = Path.Combine(Path.GetTempPath(), "yf-background-copy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storage);
        options.StorageRoot = storage;
        return storage;
    }

    private static async Task<Seed> SeedAsync(
        MigratedTestDatabase database, string storage, bool includeFile, CancellationToken ct)
    {
        await using var conn = await database.Database.OpenAsync(ct);
        var actorId = await conn.ExecuteScalarAsync<ulong>(new CommandDefinition(
            "SELECT id FROM users WHERE employee_no='admin'", cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE users SET must_change_password=0,status='ACTIVE' WHERE id=@ActorId",
            new { ActorId = actorId }, cancellationToken: ct));
        const ulong supplierId = 99101;
        const ulong partId = 99102;
        const ulong priorityId = 99103;
        const ulong groupId = 99111;
        const ulong projectId = 99112;
        await conn.ExecuteAsync(new CommandDefinition($"""
            INSERT INTO suppliers(id,name,status,created_at,updated_at)
            VALUES({supplierId},'后台复制供应商','ACTIVE',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
            INSERT INTO robot_parts(id,supplier_id,part_number,model,sort_no,status)
            VALUES({partId},{supplierId},'ASYNC-PART','异步复制型号',1,'ACTIVE');
            INSERT INTO project_dictionaries(id,type,name,parent_id,sort_no,status)
            VALUES({priorityId},'PRIORITY','后台复制优先级',NULL,1,'ACTIVE');
            INSERT INTO project_groups(id,name,description,supplier_id,status,created_by,machine_model,robot_part_id,responsible_user_id,section_id,priority_id,expected_completion_date,created_at,updated_at)
            VALUES({groupId},'后台复制主项目','测试',{supplierId},'DRAFT',@ActorId,'M1',{partId},@ActorId,NULL,{priorityId},'2026-12-31',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
            INSERT INTO projects(id,project_group_id,name,description,supplier_id,status,created_by,machine_model,robot_part_id,responsible_user_id,section_id,priority_id,expected_completion_date,created_at,updated_at)
            VALUES({projectId},{groupId},'后台复制源项目','测试',{supplierId},'DRAFT',@ActorId,'M1',{partId},@ActorId,NULL,{priorityId},'2026-12-31',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
            INSERT INTO project_group_work_orders(project_group_id,work_order_no,sort_no) VALUES({groupId},'WO-ASYNC',0);
            INSERT INTO project_work_orders(project_id,work_order_no,sort_no) VALUES({projectId},'WO-ASYNC',0);
            """, new { ActorId = actorId }, cancellationToken: ct));

        var bytes = "durable-independent-copy"u8.ToArray();
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        const string relative = "files/2026/09/background-source.txt";
        if (includeFile)
        {
            var path = Path.Combine(storage, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, bytes, ct);
            await conn.ExecuteAsync(new CommandDefinition($"""
                INSERT INTO files(id,project_id,uploader_id,direction,original_name,stored_name,ext,size_bytes,mime_type,sha256,storage_path,status,created_at)
                VALUES(99121,{projectId},@ActorId,'C2S','后台源文件.txt','background-source.txt','txt',@Size,'text/plain',@Sha,@Path,'AVAILABLE',UTC_TIMESTAMP(3))
                """, new { ActorId = actorId, Size = bytes.Length, Sha = sha, Path = relative }, cancellationToken: ct));
        }
        return new(actorId, groupId, projectId, priorityId, bytes, sha, relative);
    }

    private static void TryDeleteStorage(string path) { try { Directory.Delete(path, recursive: true); } catch { } }

    private sealed record Seed(ulong ActorId, ulong GroupId, ulong ProjectId, ulong PriorityId,
        byte[] Bytes, string Sha256, string StoragePath);
    private sealed class CopiedFile { public string StoragePath { get; init; } = ""; public string Sha256 { get; init; } = ""; }
    private sealed class JobState { public string Status { get; init; } = ""; public string? Error { get; init; } }
    private sealed class NoOpPublisher : IProjectRealtimePublisher
    {
        public Task PublishAsync(ulong projectId, string kind, CancellationToken ct = default) => Task.CompletedTask;
    }
}
