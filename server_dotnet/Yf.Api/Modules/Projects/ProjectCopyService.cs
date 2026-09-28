using System.Diagnostics;
using System.Text;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Yf.Api.Infrastructure;
using Yf.Api.Infrastructure.Entities;
using Yf.Api.Modules.Files;

namespace Yf.Api.Modules.Projects;

internal sealed class ProjectCopyService(
    AppDb database,
    AppOptions options,
    AuditService audit,
    IProjectRealtimePublisher realtime,
    ProjectGroupStatusService groupStatus,
    ProjectCopyWakeSignal? wake = null,
    ILogger<ProjectCopyService>? logger = null)
{
    private static readonly Action<ILogger, ulong, Exception?> LogRealtimePublishFailure =
        LoggerMessage.Define<ulong>(LogLevel.Warning, new EventId(1, "ProjectCopyRealtimePublishFailed"),
            "Realtime publish failed after project copy commit for project {ProjectId}");
    private static readonly Action<ILogger, ulong, Exception?> LogExecutionFailure =
        LoggerMessage.Define<ulong>(LogLevel.Warning, new EventId(2, "ProjectCopyExecutionFailed"),
            "Project copy job {JobId} failed");

    private static readonly Action<ILogger, ulong, uint, double, Exception?> LogTransientRetry =
        LoggerMessage.Define<ulong, uint, double>(LogLevel.Warning, new EventId(3, "ProjectCopyTransientRetry"),
            "Project copy job {JobId} hit a database lock conflict; retry {Retry} in {DelaySeconds}s");

    internal async Task<ProjectCopyJobResponse> EnqueueAsync(MySqlConnection conn, CurrentUser actor,
        ulong sourceProjectId, ProjectCopyRequest request, string? ip, CancellationToken ct)
    {
        if (!options.CopyWorkerEnabled)
            throw new ApiException(503, 50301, "项目复制服务当前未启用");
        if (!actor.IsInternal) throw ApiException.Forbidden("仅内部用户可以复制项目");
        var targetName = ValidateName(request.Name);
        var idempotencyKey = ValidateIdempotencyKey(request.IdempotencyKey);
        if (conn.State == System.Data.ConnectionState.Closed) await conn.OpenAsync(ct);
        await using var lease = await MySqlNamedLock.TryAcquireAsync(conn,
            MySqlNamedLock.Name("project-copy-submit", conn.Database, actor.Id, idempotencyKey), 10, ct)
            ?? throw ApiException.Busy("复制请求正在提交，请稍后重试");
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        await AccessService.LockBusinessAsync(conn, tx, ct);
        var current = await AccessService.RecheckActorAsync(conn, tx, actor, ct);
        if (!current.IsInternal) throw ApiException.Forbidden("仅内部用户可以复制项目");
        await AccessService.RequirePermissionAsync(conn, tx, current, "project:list", ct);
        await AccessService.RequirePermissionAsync(conn, tx, current, "project:create", ct);
        await using var db = EfDb.Use(conn, tx);
        var existing = await db.ProjectCopyJobs.SingleOrDefaultAsync(
            job => job.RequestedBy == current.Id && job.IdempotencyKey == idempotencyKey, ct);
        if (existing is not null)
        {
            if (existing.SourceProjectId != sourceProjectId
                || !string.Equals(existing.TargetName, targetName, StringComparison.Ordinal))
                throw ApiException.Conflict("幂等键已用于不同的复制请求");
            await ProjectAccessService.RequireViewForValidatedActorAsync(
                conn, tx, current, existing.SourceProjectId, false, ct);
            await tx.CommitAsync(ct);
            return JobResponse(existing);
        }

        await ProjectAccessService.RequireViewForValidatedActorAsync(conn, tx, current, sourceProjectId, true, ct);
        var source = await LoadProjectAsync(db, sourceProjectId, ct);
        var group = await LoadGroupAsync(db, source.ProjectGroupId, ct);
        if (string.Equals(source.Name, targetName, StringComparison.OrdinalIgnoreCase))
            throw ApiException.BadRequest("复制项目必须使用新的项目名称");
        await EnsureNameUniqueAsync(db, targetName, ct);
        await ValidateSourceAsync(db, source, ct);
        await ValidateGroupAsync(db, group, ct);
        if (group.Status is ProjectStatuses.Completed or ProjectStatuses.Terminated)
            throw ApiException.Conflict("主项目已结束，不能复制子项目");
        if (await ActiveUploadCountAsync(db, sourceProjectId, ct) != 0)
            throw ApiException.Conflict("源项目仍有进行中的文件上传，请上传完成后再复制");
        // Enqueue only records an advisory total. Capture/commit perform the authoritative locked
        // snapshot, so locking every source file here only blocks uploads and deletes unnecessarily.
        var files = await LoadFilesAsync(db, sourceProjectId, ct);
        var available = files.Where(file => file.Status == FileStatuses.Available).ToArray();
        var totalBytes = TotalBytes(available);
        var now = await DatabaseUtcNowAsync(db, ct);
        var job = new ProjectCopyJob
        {
            SourceProjectId = sourceProjectId,
            ProjectGroupId = group.Id,
            RequestedBy = current.Id,
            IdempotencyKey = idempotencyKey,
            TargetName = targetName,
            RequestIp = string.IsNullOrWhiteSpace(ip) ? "unknown" : ip[..Math.Min(ip.Length, 64)],
            Status = ProjectCopyJobStatuses.Pending,
            FilesTotal = (ulong)available.Length,
            BytesTotal = totalBytes,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.ProjectCopyJobs.Add(job);
        await db.SaveChangesAsync(ct);
        // Once the durable row exists, finish the commit even if the client disconnected.
        await tx.CommitAsync(CancellationToken.None);
        wake?.Ring();
        return JobResponse(job);
    }

    internal async Task<ProjectCopyJobResponse> GetJobAsync(MySqlConnection conn, CurrentUser actor,
        ulong jobId, CancellationToken ct)
    {
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        var current = await AccessService.ReadActorAsync(conn, tx, actor, ct);
        await AccessService.RequirePermissionAsync(conn, tx, current, "project:list", ct);
        await using var db = EfDb.Use(conn, tx);
        var job = await db.ProjectCopyJobs.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == jobId && item.RequestedBy == current.Id, ct)
            ?? throw ApiException.NotFound();
        await ProjectAccessService.RequireViewForValidatedActorAsync(
            conn, tx, current, job.SourceProjectId, false, ct);
        await tx.CommitAsync(ct);
        return JobResponse(job);
    }

    internal async Task<ProjectCopyJobListResponse> ListJobsAsync(MySqlConnection conn, CurrentUser actor,
        ulong groupId, CancellationToken ct)
    {
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        var current = await AccessService.ReadActorAsync(conn, tx, actor, ct);
        await ProjectGroupAccessService.RequireViewAsync(conn, tx, current, groupId, false, ct);
        await using var db = EfDb.Use(conn, tx);
        var visibleProjects = await ProjectAccessService.VisibleQueryAsync(db, current, ct);
        var activeStatuses = new[] { ProjectCopyJobStatuses.Pending, ProjectCopyJobStatuses.Running };
        var jobs = db.ProjectCopyJobs.AsNoTracking()
            .Where(job => job.ProjectGroupId == groupId && job.RequestedBy == current.Id
                && visibleProjects.Any(project => project.Id == job.SourceProjectId));
        var active = await jobs.Where(job => activeStatuses.Contains(job.Status))
            .OrderByDescending(job => job.CreatedAt).ThenByDescending(job => job.Id).ToArrayAsync(ct);
        var recent = await jobs.Where(job => !activeStatuses.Contains(job.Status))
            .OrderByDescending(job => job.CreatedAt).ThenByDescending(job => job.Id).Take(20).ToArrayAsync(ct);
        var visible = active.Concat(recent).OrderByDescending(job => job.CreatedAt).ThenByDescending(job => job.Id)
            .Select(JobResponse).ToArray();
        await tx.CommitAsync(ct);
        return new ProjectCopyJobListResponse(visible);
    }

    internal async Task ExecuteJobAsync(
        ulong jobId, string expectedExecutionToken, ulong expectedWorkerEpoch, CancellationToken ct)
    {
        ProjectCopyJob execution;
        await using (var conn = await database.OpenAsync(ct))
        await using (var db = EfDb.Use(conn))
            execution = await db.ProjectCopyJobs.AsNoTracking().SingleOrDefaultAsync(job => job.Id == jobId, ct)
                ?? throw new InvalidOperationException($"Project copy job {jobId} disappeared.");
        if (execution.Status == ProjectCopyJobStatuses.Succeeded) return;
        if (execution.Status != ProjectCopyJobStatuses.Running
            || execution.ExecutionToken != expectedExecutionToken
            || execution.WorkerEpoch != expectedWorkerEpoch)
            throw new InvalidOperationException($"Project copy job {jobId} is not running.");
        var executionToken = expectedExecutionToken;
        var actor = new CurrentUser(execution.RequestedBy, string.Empty, UserTypes.Internal, null);
        try
        {
            CleanupExecutionDirectory(jobId, executionToken, ct);
            await using var conn = await database.OpenAsync(ct);
            await CopyCoreAsync(conn, actor, execution.SourceProjectId,
                new ProjectCopyRequest { Name = execution.TargetName, IdempotencyKey = execution.IdempotencyKey },
                execution.RequestIp, jobId, executionToken, expectedWorkerEpoch, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (await ReturnToPendingAsync(jobId, executionToken, expectedWorkerEpoch))
                CleanupExecutionDirectory(jobId, executionToken, CancellationToken.None);
            throw;
        }
        catch (ProjectCopyCommitOutcomeUnknownException unknown)
        {
            // The original connection and transaction are disposed before this catch. Locking the job row
            // now waits for MySQL to settle the original COMMIT, so only this stable read may recover a
            // negative outcome. A database outage leaves RUNNING intact for the next lease owner.
            if (!await ResolveUnknownOutcomeAsync(unknown, CancellationToken.None)) throw;
            CleanupExecutionDirectory(jobId, executionToken, CancellationToken.None);
        }
        catch (Exception error) when (ProjectCopyRetryPolicy.RetryDelay(error, execution.RetryCount) is { } delay)
        {
            // Deadlock / lock-wait timeout: the copy transaction rolled back as a whole, so queue it again.
            if (logger is not null) LogTransientRetry(logger, jobId, execution.RetryCount + 1, delay.TotalSeconds, error);
            if (await RetryLaterAsync(jobId, executionToken, expectedWorkerEpoch, delay, CancellationToken.None))
            {
                CleanupExecutionDirectory(jobId, executionToken, CancellationToken.None);
                return;
            }
            throw new InvalidOperationException(
                "Project copy job retry could not be persisted; the lease cycle must recover it.", error);
        }
        catch (Exception error)
        {
            if (logger is not null) LogExecutionFailure(logger, jobId, error);
            if (await FailJobAsync(jobId, executionToken, expectedWorkerEpoch, SafeError(error), CancellationToken.None))
            {
                CleanupExecutionDirectory(jobId, executionToken, CancellationToken.None);
                return;
            }
            throw new InvalidOperationException(
                "Project copy job failure could not be persisted; the lease cycle must recover it.", error);
        }
    }

    internal Task<ProjectCopyResponse> CopyAsync(MySqlConnection conn, CurrentUser actor, ulong sourceProjectId,
        ProjectCopyRequest request, string? ip, CancellationToken ct) =>
        CopyCoreAsync(conn, actor, sourceProjectId, request, ip, null, null, null, ct);

    private async Task<ProjectCopyResponse> CopyCoreAsync(MySqlConnection conn, CurrentUser actor, ulong sourceProjectId,
        ProjectCopyRequest request, string? ip, ulong? jobId, string? executionToken,
        ulong? workerEpoch, CancellationToken ct)
    {
        if (!actor.IsInternal) throw ApiException.Forbidden("仅内部用户可以复制项目");
        var targetName = ValidateName(request.Name);
        // A previous failed attempt may have released this caller-owned connection.
        if (conn.State == System.Data.ConnectionState.Closed) await conn.OpenAsync(ct);
        var snapshot = await CaptureSnapshotAsync(conn, actor, sourceProjectId, targetName, ct);
        // Release the snapshot connection before validating immutable blob files. The commit phase
        // opens it again, locks each content hash, and rechecks access and the complete source snapshot.
        await conn.CloseAsync();
        var availableFiles = snapshot.Files.Where(file => file.Status == FileStatuses.Available).ToArray();
        var totalBytes = TotalBytes(availableFiles);

        var root = FileStorage.Root(options.StorageRoot);
        var prepared = new List<PreparedCopy>(availableFiles.Length);
        var committed = false;
        var commitOutcomeUnknown = false;
        ulong targetProjectId = 0;
        ulong copyId = 0;
        ProjectCopyResponse? response = null;
        try
        {
            if (jobId is ulong progressJobId)
            {
                // Reuse one connection for progress, then release it before reopening the commit
                // connection. This keeps the worker compatible with deliberately small pools.
                await using var progressConnection = await database.OpenAsync(ct);
                await UpdateTotalsAsync(progressConnection, progressJobId, executionToken!, workerEpoch!.Value,
                    (ulong)availableFiles.Length, totalBytes, ct);
                await PrepareFilesAsync(progressConnection, progressJobId);
            }
            else await PrepareFilesAsync(null, null);

            // The short commit transaction fences permission changes, proves the source snapshot is
            // unchanged and serializes blob reference creation with last-reference garbage collection.
            await conn.OpenAsync(ct);
            await using var blobLeases = await FileBlobStore.AcquireAsync(
                conn, prepared.Select(item => item.Blob.Sha256), ct);
            await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
            await AccessService.LockBusinessAsync(conn, tx, ct);
            var current = await AccessService.RecheckActorAsync(conn, tx, actor, ct);
            if (!current.IsInternal) throw ApiException.Forbidden("仅内部用户可以复制项目");
            await AccessService.RequirePermissionAsync(conn, tx, current, "project:list", ct);
            await AccessService.RequirePermissionAsync(conn, tx, current, "project:create", ct);
            await ProjectAccessService.RequireViewForValidatedActorAsync(conn, tx, current, sourceProjectId, true, ct);
            await using var db = EfDb.Use(conn, tx);
            ProjectCopyJob? job = null;
            if (jobId is ulong ownedJobId)
            {
                var state = await db.ProjectCopyWorkerStates
                    .FromSqlRaw("SELECT * FROM project_copy_worker_state WHERE id=1 FOR UPDATE")
                    .SingleAsync(ct);
                if (state.Epoch != workerEpoch)
                    throw ApiException.Conflict("项目复制执行权已转移，将由当前工作器恢复");
                job = await db.ProjectCopyJobs
                    .FromSqlInterpolated($"SELECT * FROM project_copy_jobs WHERE id={ownedJobId} FOR UPDATE")
                    .SingleOrDefaultAsync(ct) ?? throw new InvalidOperationException("项目复制任务不存在");
                if (job.Status != ProjectCopyJobStatuses.Running || job.RequestedBy != current.Id
                    || job.SourceProjectId != sourceProjectId || job.TargetName != targetName
                    || job.ExecutionToken != executionToken || job.WorkerEpoch != workerEpoch)
                    throw ApiException.Conflict("项目复制任务状态已变化");
            }
            var currentSource = await LoadProjectAsync(db, sourceProjectId, ct);
            var currentGroup = await LoadGroupAsync(db, currentSource.ProjectGroupId, ct);
            var currentFiles = await LoadFilesForUpdateAsync(db, sourceProjectId, ct);
            if (!SameProjectSnapshot(snapshot.Project, currentSource)
                || !SameGroupSnapshot(snapshot.Group, currentGroup)
                || !SameFileSnapshot(snapshot.Files, currentFiles)
                || await ActiveUploadCountAsync(db, sourceProjectId, ct) != 0)
                throw ApiException.Conflict("源项目或文件已发生变化，请刷新后重新复制");
            await ValidateSourceAsync(db, currentSource, ct);
            await ValidateGroupAsync(db, currentGroup, ct);
            if (currentGroup.Status is ProjectStatuses.Completed or ProjectStatuses.Terminated)
                throw ApiException.Conflict("主项目已结束，不能复制子项目");
            await EnsureNameUniqueAsync(db, targetName, ct);
            var createdAt = await DatabaseUtcNowAsync(db, ct);

            var resolvedBlobs = new Dictionary<string, FileBlob>(StringComparer.Ordinal);
            foreach (var group in prepared.GroupBy(item => item.Blob.Sha256, StringComparer.Ordinal)
                         .OrderBy(group => group.Key, StringComparer.Ordinal))
            {
                var expectedIds = group.Where(item => item.Source.BlobId is not null)
                    .Select(item => item.Source.BlobId!.Value).Distinct().ToArray();
                if (expectedIds.Length > 1)
                    throw ApiException.Conflict("相同内容关联了不同 blob 记录，无法复制");
                var reference = group.First();
                var blob = await FileBlobStore.ResolveForReferenceAsync(db, root, reference.Blob,
                    expectedIds.Length == 0 ? null : expectedIds[0], createdAt, ct);
                resolvedBlobs.Add(group.Key, blob);
            }

            var targetProject = new Project
            {
                ProjectGroupId = currentGroup.Id,
                Name = targetName,
                Description = currentSource.Description,
                SupplierId = currentGroup.SupplierId,
                Status = ProjectStatuses.Draft,
                ConfirmSide = null,
                CreatedBy = current.Id,
                CreatedAt = createdAt,
                UpdatedAt = createdAt,
                MachineModel = currentGroup.MachineModel,
                RobotPartId = currentGroup.RobotPartId,
                LegacyRobotModelName = currentGroup.LegacyRobotModelName,
                ResponsibleUserId = currentGroup.ResponsibleUserId,
                SectionId = currentGroup.SectionId,
                PriorityId = currentGroup.PriorityId,
                ExpectedCompletionDate = ToDateOnly(currentGroup.ExpectedCompletionDate),
            };
            db.Projects.Add(targetProject);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException error) when (error.InnerException is MySqlException { Number: 1062 })
            { throw ApiException.Conflict("子项目名称已存在"); }
            targetProjectId = targetProject.Id;
            // Project.UpdatedAt is database-generated in the shared EF model, so its explicit
            // initializer is omitted from INSERT. Override only this copied row with the same
            // database UTC timestamp used by created_at; CURRENT_TIMESTAMP may use server local time.
            var timestamped = await db.Projects.Where(project => project.Id == targetProjectId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(project => project.UpdatedAt, createdAt), ct);
            if (timestamped != 1)
                throw new InvalidOperationException("复制项目时间戳写入失败");

            var actorName = await db.Users.Where(user => user.Id == current.Id)
                .Select(user => user.RealName).SingleAsync(ct);
            var copy = new ProjectCopy
            {
                SourceProjectId = sourceProjectId,
                TargetProjectId = targetProjectId,
                SourceProjectName = currentSource.Name,
                TargetProjectName = targetName,
                CopiedBy = current.Id,
                CopiedByName = actorName,
                FileCount = (ulong)prepared.Count,
                TotalBytes = totalBytes,
                CreatedAt = createdAt,
            };
            db.ProjectCopies.Add(copy);
            db.ProjectWorkOrders.AddRange(currentGroup.WorkOrderNos.Select((value, index) => new ProjectWorkOrder
            {
                ProjectId = targetProjectId,
                WorkOrderNo = value,
                SortNo = index,
                CreatedAt = createdAt,
            }));
            db.ProjectStatusLogs.Add(new ProjectStatusLog
            {
                ProjectId = targetProjectId,
                FromStatus = null,
                ToStatus = ProjectStatuses.Draft,
                Action = "COPY",
                OperatorId = current.Id,
                ConfirmSide = null,
                Reason = null,
                CreatedAt = createdAt,
            });
            var copiedFiles = prepared.Select(item => new FileRecord
            {
                ProjectId = targetProjectId,
                BlobId = resolvedBlobs[item.Blob.Sha256].Id,
                UploaderId = item.Source.UploaderId,
                Direction = item.Source.Direction,
                OriginalName = item.Source.OriginalName,
                StoredName = item.StoredName,
                Ext = item.Source.Ext,
                SizeBytes = item.Source.SizeBytes,
                MimeType = item.Source.MimeType,
                Sha256 = item.Source.Sha256,
                StoragePath = resolvedBlobs[item.Blob.Sha256].StoragePath,
                Status = FileStatuses.Available,
                DeletedAt = null,
                CreatedAt = createdAt,
            }).ToArray();
            db.Files.AddRange(copiedFiles);
            await db.SaveChangesAsync(ct);
            copyId = copy.Id;

            db.FileCopyRefs.AddRange(prepared.Zip(copiedFiles).Select(pair => new FileCopyRef
            {
                CopyId = copyId,
                SourceFileId = pair.First.Source.Id,
                TargetFileId = pair.Second.Id,
                SourceFileName = pair.First.Source.OriginalName,
                TargetFileName = pair.First.Source.OriginalName,
                CreatedAt = createdAt,
            }));
            InsertActivities(db, sourceProjectId, targetProjectId, copyId, current.Id, actorName,
                prepared.Count, createdAt);
            await db.SaveChangesAsync(ct);

            await audit.WriteAsync(conn, tx, current.Id, "PROJECT_COPY", "project", targetProjectId, new
            {
                copyId, source = Snapshot(currentSource),
                target = new { id = targetProjectId, name = targetName, status = ProjectStatuses.Draft,
                    currentGroup.SupplierId, currentGroup.WorkOrderNos, currentGroup.MachineModel,
                    currentGroup.RobotPartId, currentGroup.ResponsibleUserId,
                    currentGroup.SectionId, currentGroup.PriorityId, currentGroup.ExpectedCompletionDate },
                fileCount = prepared.Count, totalBytes,
            }, ip, ct);
            await groupStatus.RecalculateAsync(
                conn, tx, currentGroup.Id, current.Id, targetProjectId, ct, groupAlreadyLocked: true);

            var target = await LoadProjectAsync(db, targetProjectId, ct);
            target.HasCopyHistory = true;
            target.CopySourceProjectId = sourceProjectId;
            target.CopySourceProjectName = currentSource.Name;
            response = new ProjectCopyResponse(ProjectJson.Project(target), new ProjectCopyRecord(
                copyId, sourceProjectId, targetProjectId, prepared.Count, totalBytes, ProjectJson.Utc(createdAt)));
            if (job is not null)
            {
                job.Status = ProjectCopyJobStatuses.Succeeded;
                job.FilesTotal = (ulong)prepared.Count;
                job.FilesCopied = (ulong)prepared.Count;
                job.BytesTotal = totalBytes;
                job.BytesCopied = totalBytes;
                job.Error = null;
                job.ResultProjectId = targetProjectId;
                job.ResultCopyId = copyId;
                job.ResultCopyFileCount = (ulong)prepared.Count;
                job.ExecutionToken = null;
                job.CompletedAt = createdAt;
                job.UpdatedAt = createdAt;
                await db.SaveChangesAsync(ct);
            }
            try { await tx.CommitAsync(ct); committed = true; }
            catch (Exception commitError)
            {
                var outcome = jobId is ulong committedJobId
                    ? await CheckJobCommitOutcomeAsync(committedJobId, targetProjectId, copyId)
                    : await CheckCommitOutcomeAsync(targetProjectId, copyId);
                if (outcome == true) committed = true;
                else
                {
                    commitOutcomeUnknown = true;
                    if (jobId is ulong unknownJobId)
                        throw new ProjectCopyCommitOutcomeUnknownException(
                            unknownJobId, executionToken!, workerEpoch!.Value, targetProjectId, copyId, commitError);
                    throw new InvalidOperationException("项目复制提交结果未知；已保留候选文件以避免破坏可能已提交的数据，请按复制记录核对后处理。", commitError);
                }
            }
            if (committed)
            {
                if (jobId is ulong committedJobId)
                    CleanupExecutionDirectory(committedJobId, executionToken!, CancellationToken.None);
            }
        }
        finally
        {
            if (!committed && !commitOutcomeUnknown)
            {
                if (jobId is ulong ownedJobId)
                    CleanupExecutionDirectory(ownedJobId, executionToken!, CancellationToken.None);
            }
        }

        await PublishCommittedAsync(sourceProjectId);
        await PublishCommittedAsync(targetProjectId);
        return response!;

        async Task PrepareFilesAsync(MySqlConnection? progressConnection, ulong? activeJobId)
        {
            ulong completedBytes = 0;
            var progressInterval = Stopwatch.StartNew();
            foreach (var sourceFile in availableFiles)
            {
                prepared.Add(PrepareBlobReference(root, sourceFile, ct));
                completedBytes = checked(completedBytes + sourceFile.SizeBytes);
                if (activeJobId is not ulong currentJobId
                    || prepared.Count != availableFiles.Length && prepared.Count % 10 != 0
                        && progressInterval.Elapsed < TimeSpan.FromSeconds(1))
                    continue;
                await UpdateProgressAsync(progressConnection!, currentJobId, executionToken!, workerEpoch!.Value,
                    (ulong)prepared.Count, completedBytes, ct);
                progressInterval.Restart();
            }
        }
    }

    internal async Task<ProjectCopyHistoryResponse> HistoryAsync(MySqlConnection conn, CurrentUser actor, ulong projectId, CancellationToken ct)
    {
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        var current = await AccessService.ReadActorAsync(conn, tx, actor, ct);
        await ProjectAccessService.RequireViewForValidatedActorAsync(conn, tx, current, projectId, false, ct);
        await using var db = EfDb.Use(conn, tx);
        var rows = await (from copy in db.ProjectCopies
            join source in db.Projects on copy.SourceProjectId equals source.Id
            join target in db.Projects on copy.TargetProjectId equals target.Id
            where copy.SourceProjectId == projectId || copy.TargetProjectId == projectId
            orderby copy.CreatedAt descending, copy.Id descending
            select new CopyHistoryRow
            {
                CopyId = copy.Id,
                SourceProjectId = copy.SourceProjectId,
                SourceName = copy.SourceProjectName,
                SourceSupplierId = source.SupplierId,
                SourceResponsibleUserId = source.ResponsibleUserId,
                SourceGroupCreatorId = db.ProjectGroups.Where(mainProject => mainProject.Id == source.ProjectGroupId)
                    .Select(mainProject => mainProject.CreatedBy).FirstOrDefault(),
                TargetProjectId = copy.TargetProjectId,
                TargetName = copy.TargetProjectName,
                TargetSupplierId = target.SupplierId,
                TargetResponsibleUserId = target.ResponsibleUserId,
                TargetGroupCreatorId = db.ProjectGroups.Where(mainProject => mainProject.Id == target.ProjectGroupId)
                    .Select(mainProject => mainProject.CreatedBy).FirstOrDefault(),
                CopiedByName = copy.CopiedByName,
                FileCount = copy.FileCount,
                TotalBytes = copy.TotalBytes,
                CreatedAt = copy.CreatedAt,
            }).ToArrayAsync(ct);
        ProjectCopyHistoryItem? sourceItem = null;
        var copies = new List<ProjectCopyHistoryItem>();
        var restricted = false;
        var canViewAll = current.IsInternal
            && await ProjectAccessService.HasPermissionAsync(db, current.Id, "project:view_all", ct);
        foreach (var row in rows)
        {
            var relatedId = row.SourceProjectId == projectId ? row.TargetProjectId : row.SourceProjectId;
            var relatedSupplierId = relatedId == row.SourceProjectId ? row.SourceSupplierId : row.TargetSupplierId;
            var relatedResponsibleId = relatedId == row.SourceProjectId ? row.SourceResponsibleUserId : row.TargetResponsibleUserId;
            var relatedGroupCreatorId = relatedId == row.SourceProjectId ? row.SourceGroupCreatorId : row.TargetGroupCreatorId;
            var canViewRelated = current.IsInternal
                ? canViewAll || relatedResponsibleId == current.Id || relatedGroupCreatorId == current.Id
                : current.SupplierId is not null && current.SupplierId == relatedSupplierId;
            if (!canViewRelated) { restricted = true; continue; }
            var item = HistoryItem(row, relatedId, relatedId == row.SourceProjectId ? row.SourceName : row.TargetName);
            if (row.TargetProjectId == projectId) sourceItem = item; else copies.Add(item);
        }
        await tx.CommitAsync(ct);
        return new ProjectCopyHistoryResponse(sourceItem, copies, restricted);
    }

    internal async Task<PageResponse<FileCopyHistoryItem>> FileHistoryAsync(MySqlConnection conn, CurrentUser actor, ulong copyId,
        ulong page, ulong pageSize, CancellationToken ct)
    {
        var (actualPage, size) = ProjectJson.ClampPage(page, pageSize);
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        var current = await AccessService.ReadActorAsync(conn, tx, actor, ct);
        await using var db = EfDb.Use(conn, tx);
        var relation = await db.ProjectCopies.Where(copy => copy.Id == copyId)
            .Select(copy => new ProjectCopyIds(copy.SourceProjectId, copy.TargetProjectId))
            .SingleOrDefaultAsync(ct);
        if (relation is null || !await CanViewAsync(conn, tx, current, relation.SourceProjectId, ct)
            || !await CanViewAsync(conn, tx, current, relation.TargetProjectId, ct)) throw ApiException.NotFound();
        var query = db.FileCopyRefs.Where(reference => reference.CopyId == copyId);
        var total = (ulong)await query.LongCountAsync(ct);
        var offset = (actualPage - 1) * size;
        var rows = await query.OrderBy(reference => reference.Id).Page(offset, size)
            .Select(reference => new FileCopyHistoryRow
            {
                SourceFileId = reference.SourceFileId,
                SourceFileName = reference.SourceFileName,
                TargetFileId = reference.TargetFileId,
                TargetFileName = reference.TargetFileName,
                SourceStatus = db.Files.Where(file => file.Id == reference.SourceFileId)
                    .Select(file => file.Status).FirstOrDefault(),
                TargetStatus = db.Files.Where(file => file.Id == reference.TargetFileId)
                    .Select(file => file.Status).FirstOrDefault(),
            }).ToArrayAsync(ct);
        var list = rows.Select(row => new FileCopyHistoryItem(row.SourceFileId, row.SourceFileName,
            row.SourceStatus != FileStatuses.Available, row.TargetFileId, row.TargetFileName,
            row.TargetStatus != FileStatuses.Available)).ToArray();
        await tx.CommitAsync(ct);
        return new PageResponse<FileCopyHistoryItem>(list, total, actualPage, size);
    }

    private static async Task<ProjectCopySnapshot> CaptureSnapshotAsync(MySqlConnection conn, CurrentUser actor,
        ulong sourceProjectId, string targetName, CancellationToken ct)
    {
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        var current = await AccessService.LockActorAsync(conn, tx, actor, ct);
        if (!current.IsInternal) throw ApiException.Forbidden("仅内部用户可以复制项目");
        await AccessService.RequirePermissionAsync(conn, tx, current, "project:list", ct);
        await AccessService.RequirePermissionAsync(conn, tx, current, "project:create", ct);
        await ProjectAccessService.RequireViewForValidatedActorAsync(conn, tx, current, sourceProjectId, true, ct);
        await using var db = EfDb.Use(conn, tx);
        var source = await LoadProjectAsync(db, sourceProjectId, ct);
        var group = await LoadGroupAsync(db, source.ProjectGroupId, ct);
        if (string.Equals(source.Name, targetName, StringComparison.OrdinalIgnoreCase))
            throw ApiException.BadRequest("复制项目必须使用新的项目名称");
        await EnsureNameUniqueAsync(db, targetName, ct);
        await ValidateSourceAsync(db, source, ct);
        await ValidateGroupAsync(db, group, ct);
        if (group.Status is ProjectStatuses.Completed or ProjectStatuses.Terminated)
            throw ApiException.Conflict("主项目已结束，不能复制子项目");
        if (await ActiveUploadCountAsync(db, sourceProjectId, ct) != 0)
            throw ApiException.Conflict("源项目仍有进行中的文件上传，请上传完成后再复制");
        var files = await LoadFilesForUpdateAsync(db, sourceProjectId, ct);
        await tx.CommitAsync(ct);
        return new(source, group, files);
    }

    private static PreparedCopy PrepareBlobReference(
        string root, CopyFileRow sourceFile, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sourceFile.Sha256) || sourceFile.Sha256.Length != 64)
            throw ApiException.Conflict($"源文件“{sourceFile.OriginalName}”缺少完整性校验值，无法复制");
        var storedName = $"{Guid.NewGuid():D}.{sourceFile.Ext}";
        if (sourceFile.BlobId is null)
            throw ApiException.Conflict($"源文件“{sourceFile.OriginalName}”尚未完成内容存储转换，无法复制");
        string sha256;
        try
        {
            sha256 = FileBlobStore.NormalizeSha256(sourceFile.Sha256);
            FileBlobStore.VerifyBoundPhysicalFile(root, sourceFile.StoragePath, sha256,
                sourceFile.SizeBytes, ct);
        }
        catch (InvalidOperationException)
        {
            throw ApiException.Conflict($"源文件“{sourceFile.OriginalName}”内容引用异常，无法复制");
        }
        return new(sourceFile, storedName,
            new FileBlobStore.PreparedBlob(sha256, sourceFile.SizeBytes, null));
    }

    private async Task<bool?> CheckCommitOutcomeAsync(ulong targetProjectId, ulong copyId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await using var verification = await database.OpenAsync(timeout.Token);
            await using var db = EfDb.Use(verification);
            return await db.ProjectCopies.AnyAsync(copy => copy.Id == copyId
                && copy.TargetProjectId == targetProjectId, timeout.Token) ? true : null;
        }
        catch { return null; }
    }

    internal async Task<bool?> CheckJobCommitOutcomeAsync(ulong jobId, ulong targetProjectId, ulong copyId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await using var verification = await database.OpenAsync(timeout.Token);
            await using var db = EfDb.Use(verification);
            var outcome = await db.ProjectCopyJobs.AsNoTracking().Where(job => job.Id == jobId)
                .Select(job => new { job.Status, job.ResultProjectId, job.ResultCopyId })
                .SingleOrDefaultAsync(timeout.Token);
            if (outcome is null) return false;
            if (outcome.Status == ProjectCopyJobStatuses.Succeeded)
                return outcome.ResultProjectId == targetProjectId && outcome.ResultCopyId == copyId;
            return null;
        }
        catch { return null; }
    }

    private static async Task ValidateSourceAsync(YfDbContext db, ProjectRow source, CancellationToken ct)
    {
        await ValidateCopyMetadataAsync(db, source.WorkOrderNos, source.MachineModel, source.SupplierId,
            source.RobotPartId, source.ResponsibleUserId, source.SectionId, source.PriorityId,
            source.ExpectedCompletionDate, "源项目", ct);
    }

    private static async Task ValidateGroupAsync(YfDbContext db, CopyGroupRow group, CancellationToken ct)
    {
        await ValidateCopyMetadataAsync(db, group.WorkOrderNos, group.MachineModel, group.SupplierId,
            group.RobotPartId, group.ResponsibleUserId, group.SectionId, group.PriorityId,
            group.ExpectedCompletionDate, "主项目", ct);
    }

    private static async Task ValidateCopyMetadataAsync(
        YfDbContext db, IReadOnlyCollection<string> workOrderNos, string? machineModel, ulong supplierId,
        ulong? robotPartId, ulong? ownerId, ulong? sectionId, ulong? priorityId,
        DateTime? expectedCompletionDate, string label, CancellationToken ct)
    {
        if (workOrderNos.Count == 0 || string.IsNullOrWhiteSpace(machineModel)
            || robotPartId is null or 0 || ownerId is null or 0
            || priorityId is null or 0 || expectedCompletionDate is null)
            throw ApiException.Conflict($"{label}资料不完整，请先补齐必填信息后再复制");
        if (!await ValidMetadataAsync(db, supplierId, ownerId.Value, sectionId,
                robotPartId.Value, priorityId.Value, ct))
            throw ApiException.Conflict($"{label}关联资料已失效，请先更新负责人、课别、供应商或数据字典后再复制");
    }

    private static async Task<bool> ValidMetadataAsync(YfDbContext db, ulong supplierId, ulong ownerId, ulong? sectionId,
        ulong robotPartId, ulong priorityId, CancellationToken ct)
    {
        if (!await db.Suppliers.AnyAsync(supplier => supplier.Id == supplierId && supplier.Status == AccountStatuses.Active, ct))
            return false;
        if (!await ProjectService.EligibleOwners(db)
                .AnyAsync(owner => owner.Id == ownerId && owner.SectionId == sectionId, ct))
            return false;
        if (!await db.RobotParts.AnyAsync(part => part.Id == robotPartId
                && part.SupplierId == supplierId && part.Status == AccountStatuses.Active, ct))
            return false;
        return await db.ProjectDictionaries.AnyAsync(priority => priority.Id == priorityId
            && priority.Type == ProjectDictionaryTypes.Priority && priority.Status == AccountStatuses.Active, ct);
    }

    private static async Task<ProjectRow> LoadProjectAsync(YfDbContext db, ulong projectId, CancellationToken ct)
    {
        var row = await ProjectQueries.Rows(db).SingleOrDefaultAsync(project => project.Id == projectId, ct)
            ?? throw ApiException.NotFound();
        row.WorkOrderNos = await db.ProjectWorkOrders.Where(order => order.ProjectId == projectId)
            .OrderBy(order => order.SortNo).ThenBy(order => order.Id)
            .Select(order => order.WorkOrderNo).ToArrayAsync(ct);
        return row;
    }

    private static async Task<CopyGroupRow> LoadGroupAsync(YfDbContext db, ulong groupId, CancellationToken ct)
    {
        var row = await db.ProjectGroups.Where(group => group.Id == groupId).Select(group => new CopyGroupRow
        {
            Id = group.Id,
            SupplierId = group.SupplierId,
            Status = group.Status,
            MachineModel = group.MachineModel,
            RobotPartId = group.RobotPartId,
            LegacyRobotModelName = group.LegacyRobotModelName,
            ResponsibleUserId = group.ResponsibleUserId,
            SectionId = group.SectionId,
            PriorityId = group.PriorityId,
            ExpectedCompletionDate = group.ExpectedCompletionDate.HasValue
                ? group.ExpectedCompletionDate.Value.ToDateTime(TimeOnly.MinValue) : null,
            UpdatedAt = group.UpdatedAt,
        }).SingleOrDefaultAsync(ct) ?? throw ApiException.NotFound("主项目不存在");
        row.WorkOrderNos = await db.ProjectGroupWorkOrders.Where(order => order.ProjectGroupId == groupId)
            .OrderBy(order => order.SortNo).ThenBy(order => order.Id)
            .Select(order => order.WorkOrderNo).ToArrayAsync(ct);
        return row;
    }

    private static async Task<CopyFileRow[]> LoadFilesForUpdateAsync(
        YfDbContext db, ulong projectId, CancellationToken ct)
    {
        var files = await db.Files
            .FromSqlInterpolated($"SELECT * FROM files WHERE project_id={projectId} ORDER BY id FOR UPDATE")
            .AsNoTracking().ToArrayAsync(ct);
        return files.Select(file => new CopyFileRow(file.Id, file.UploaderId, file.Direction, file.OriginalName,
            file.Ext, file.SizeBytes, file.MimeType, file.Sha256, file.StoragePath, file.Status, file.BlobId)).ToArray();
    }

    private static async Task<CopyFileRow[]> LoadFilesAsync(
        YfDbContext db, ulong projectId, CancellationToken ct)
    {
        var files = await db.Files.AsNoTracking().Where(file => file.ProjectId == projectId)
            .OrderBy(file => file.Id).ToArrayAsync(ct);
        return files.Select(file => new CopyFileRow(file.Id, file.UploaderId, file.Direction, file.OriginalName,
            file.Ext, file.SizeBytes, file.MimeType, file.Sha256, file.StoragePath, file.Status, file.BlobId)).ToArray();
    }

    private static async Task<ulong> ActiveUploadCountAsync(YfDbContext db, ulong projectId, CancellationToken ct) =>
        (ulong)await db.UploadSessions.LongCountAsync(upload => upload.ProjectId == projectId
            && (upload.Status == ProjectUploadSessionStatuses.Uploading
                || upload.Status == ProjectUploadSessionStatuses.Merging), ct);

    private static bool SameProjectSnapshot(ProjectRow a, ProjectRow b) => a.Id == b.Id
        && a.ProjectGroupId == b.ProjectGroupId && a.Name == b.Name
        && a.Description == b.Description && a.SupplierId == b.SupplierId && a.Status == b.Status
        && a.ConfirmSide == b.ConfirmSide && a.UpdatedAt == b.UpdatedAt && a.MachineModel == b.MachineModel
        && a.RobotPartId == b.RobotPartId
        && a.ResponsibleUserId == b.ResponsibleUserId && a.SectionId == b.SectionId && a.PriorityId == b.PriorityId
        && a.ExpectedCompletionDate == b.ExpectedCompletionDate && a.WorkOrderNos.SequenceEqual(b.WorkOrderNos, StringComparer.Ordinal);

    private static bool SameGroupSnapshot(CopyGroupRow a, CopyGroupRow b) => a.Id == b.Id
        && a.SupplierId == b.SupplierId && a.Status == b.Status && a.UpdatedAt == b.UpdatedAt
        && a.MachineModel == b.MachineModel && a.RobotPartId == b.RobotPartId
        && a.LegacyRobotModelName == b.LegacyRobotModelName && a.ResponsibleUserId == b.ResponsibleUserId
        && a.SectionId == b.SectionId && a.PriorityId == b.PriorityId
        && a.ExpectedCompletionDate == b.ExpectedCompletionDate
        && a.WorkOrderNos.SequenceEqual(b.WorkOrderNos, StringComparer.Ordinal);

    private static bool SameFileSnapshot(CopyFileRow[] a, CopyFileRow[] b) =>
        a.Length == b.Length && a.Zip(b).All(pair => pair.First == pair.Second);

    private static async Task EnsureNameUniqueAsync(YfDbContext db, string name, CancellationToken ct)
    {
        if (await db.Projects.AnyAsync(project => project.Name == name, ct))
            throw ApiException.Conflict("子项目名称已存在");
    }

    private static ulong TotalBytes(IEnumerable<CopyFileRow> files)
    {
        ulong total = 0;
        try { foreach (var file in files) total = checked(total + file.SizeBytes); }
        catch (OverflowException) { throw ApiException.Conflict("源项目文件总大小异常，无法复制"); }
        return total;
    }

    private static string ValidateName(string? value)
    {
        var name = (value ?? string.Empty).Trim();
        if (name.Length == 0 || name.EnumerateRunes().Count() > 128)
            throw ApiException.BadRequest("新项目名称需为 1~128 个字符");
        return name;
    }

    private static string ValidateIdempotencyKey(string? value)
    {
        var key = (value ?? string.Empty).Trim();
        if (key.Length is < 1 or > 64 || key.Any(character => !(char.IsAsciiLetterOrDigit(character)
                || character is '-' or '_' or '.' or ':')))
            throw ApiException.BadRequest("idempotencyKey 需为 1~64 位字母、数字或 - _ . :");
        return key;
    }

    private static ProjectCopyJobResponse JobResponse(ProjectCopyJob job) => new(
        job.Id, job.SourceProjectId, job.ProjectGroupId, job.TargetName, job.Status,
        job.FilesTotal, job.FilesCopied, job.BytesTotal, job.BytesCopied, job.Error,
        job.ResultProjectId is ulong projectId
            ? new ProjectCopyJobResult(projectId, job.ResultCopyFileCount ?? job.FilesCopied)
            : null,
        ProjectJson.Utc(job.CreatedAt),
        job.StartedAt is DateTime startedAt ? ProjectJson.Utc(startedAt) : null,
        job.CompletedAt is DateTime completedAt ? ProjectJson.Utc(completedAt) : null);

    private async Task UpdateTotalsAsync(
        MySqlConnection conn, ulong jobId, string executionToken, ulong workerEpoch,
        ulong filesTotal, ulong bytesTotal, CancellationToken ct)
    {
        await using var db = EfDb.Use(conn);
        var now = await DatabaseUtcNowAsync(db, ct);
        var changed = await db.ProjectCopyJobs
            .Where(job => job.Id == jobId && job.Status == ProjectCopyJobStatuses.Running
                && job.ExecutionToken == executionToken && job.WorkerEpoch == workerEpoch)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.FilesTotal, filesTotal)
                .SetProperty(job => job.FilesCopied, 0UL)
                .SetProperty(job => job.BytesTotal, bytesTotal)
                .SetProperty(job => job.BytesCopied, 0UL)
                .SetProperty(job => job.UpdatedAt, now), ct);
        if (changed != 1) throw new InvalidOperationException("项目复制任务已不再运行");
    }

    private async Task UpdateProgressAsync(
        MySqlConnection conn, ulong jobId, string executionToken, ulong workerEpoch,
        ulong filesCopied, ulong bytesCopied, CancellationToken ct)
    {
        await using var db = EfDb.Use(conn);
        var now = await DatabaseUtcNowAsync(db, ct);
        var changed = await db.ProjectCopyJobs
            .Where(job => job.Id == jobId && job.Status == ProjectCopyJobStatuses.Running
                && job.ExecutionToken == executionToken && job.WorkerEpoch == workerEpoch)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.FilesCopied, filesCopied)
                .SetProperty(job => job.BytesCopied, bytesCopied)
                .SetProperty(job => job.UpdatedAt, now), ct);
        if (changed != 1) throw new InvalidOperationException("项目复制任务已不再运行");
    }

    private async Task<bool> FailJobAsync(
        ulong jobId, string executionToken, ulong workerEpoch, string error, CancellationToken ct)
    {
        try
        {
            await using var conn = await database.OpenAsync(ct);
            await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
            await using var db = EfDb.Use(conn, tx);
            var state = await db.ProjectCopyWorkerStates
                .FromSqlRaw("SELECT * FROM project_copy_worker_state WHERE id=1 FOR UPDATE")
                .SingleAsync(ct);
            if (state.Epoch != workerEpoch)
            {
                await tx.CommitAsync(ct);
                return false;
            }
            var now = await DatabaseUtcNowAsync(db, ct);
            var changed = await db.ProjectCopyJobs
                .Where(job => job.Id == jobId && job.Status == ProjectCopyJobStatuses.Running
                    && job.ExecutionToken == executionToken && job.WorkerEpoch == workerEpoch)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(job => job.Status, ProjectCopyJobStatuses.Failed)
                    .SetProperty(job => job.ExecutionToken, (string?)null)
                    .SetProperty(job => job.Error, error)
                    .SetProperty(job => job.CompletedAt, now)
                    .SetProperty(job => job.UpdatedAt, now), ct);
            await tx.CommitAsync(ct);
            return changed == 1;
        }
        catch { return false; }
    }

    private async Task<bool> RetryLaterAsync(
        ulong jobId, string executionToken, ulong workerEpoch, TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await using var conn = await database.OpenAsync(ct);
            await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
            await using var db = EfDb.Use(conn, tx);
            var state = await db.ProjectCopyWorkerStates
                .FromSqlRaw("SELECT * FROM project_copy_worker_state WHERE id=1 FOR UPDATE")
                .SingleAsync(ct);
            if (state.Epoch != workerEpoch)
            {
                await tx.CommitAsync(ct);
                return false;
            }
            var now = await DatabaseUtcNowAsync(db, ct);
            var notBefore = now + delay;
            var changed = await db.ProjectCopyJobs
                .Where(job => job.Id == jobId && job.Status == ProjectCopyJobStatuses.Running
                    && job.ExecutionToken == executionToken && job.WorkerEpoch == workerEpoch)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(job => job.Status, ProjectCopyJobStatuses.Pending)
                    .SetProperty(job => job.ExecutionToken, (string?)null)
                    .SetProperty(job => job.FilesCopied, 0UL)
                    .SetProperty(job => job.BytesCopied, 0UL)
                    .SetProperty(job => job.Error, (string?)null)
                    .SetProperty(job => job.RetryCount, job => job.RetryCount + 1)
                    .SetProperty(job => job.NextAttemptAt, notBefore)
                    .SetProperty(job => job.UpdatedAt, now), ct);
            await tx.CommitAsync(ct);
            return changed == 1;
        }
        catch { return false; }
    }

    private async Task<bool> ReturnToPendingAsync(ulong jobId, string executionToken, ulong workerEpoch)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await using var conn = await database.OpenAsync(timeout.Token);
            await using var db = EfDb.Use(conn);
            var now = await DatabaseUtcNowAsync(db, timeout.Token);
            return await db.ProjectCopyJobs
                .Where(job => job.Id == jobId && job.Status == ProjectCopyJobStatuses.Running
                    && job.ExecutionToken == executionToken && job.WorkerEpoch == workerEpoch)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(job => job.Status, ProjectCopyJobStatuses.Pending)
                    .SetProperty(job => job.ExecutionToken, (string?)null)
                    .SetProperty(job => job.FilesCopied, 0UL)
                    .SetProperty(job => job.BytesCopied, 0UL)
                    .SetProperty(job => job.Error, (string?)null)
                    .SetProperty(job => job.UpdatedAt, now), timeout.Token) == 1;
        }
        catch { return false; }
    }

    private void CleanupExecutionDirectory(ulong jobId, string executionToken, CancellationToken ct)
    {
        var root = FileStorage.Root(options.StorageRoot);
        var directory = FileStorage.EnsureLexicallyWithin(root,
            Path.Combine(root, "copy-jobs", jobId.ToString(System.Globalization.CultureInfo.InvariantCulture), executionToken),
            allowRoot: false);
        FileStorage.DeleteDirectoryTree(root, directory, ct);
    }

    private async Task<bool> ResolveUnknownOutcomeAsync(
        ProjectCopyCommitOutcomeUnknownException unknown, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var mayCleanup = false;
        try
        {
            await using (var conn = await database.OpenAsync(timeout.Token))
            await using (var tx = await AppDb.BeginTransactionAsync(conn, timeout.Token))
            await using (var db = EfDb.Use(conn, tx))
            {
                var state = await db.ProjectCopyWorkerStates
                    .FromSqlRaw("SELECT * FROM project_copy_worker_state WHERE id=1 FOR UPDATE")
                    .SingleAsync(timeout.Token);
                if (state.Epoch != unknown.WorkerEpoch)
                {
                    await tx.CommitAsync(timeout.Token);
                    return false;
                }
                // FOR UPDATE waits for the original transaction to commit or roll back. A plain negative
                // read is insufficient because it may race an in-flight COMMIT whose acknowledgement was lost.
                var job = await db.ProjectCopyJobs
                    .FromSqlInterpolated($"SELECT * FROM project_copy_jobs WHERE id={unknown.JobId} FOR UPDATE")
                    .SingleOrDefaultAsync(timeout.Token);
                if (job is null)
                {
                    await tx.CommitAsync(timeout.Token);
                    return true;
                }
                if (job.Status == ProjectCopyJobStatuses.Succeeded)
                {
                    var matches = job.ResultProjectId == unknown.TargetProjectId
                        && job.ResultCopyId == unknown.CopyId;
                    await tx.CommitAsync(timeout.Token);
                    return matches;
                }
                if (job.Status == ProjectCopyJobStatuses.Running
                    && job.ExecutionToken == unknown.ExecutionToken
                    && job.WorkerEpoch == unknown.WorkerEpoch)
                {
                    var now = await DatabaseUtcNowAsync(db, timeout.Token);
                    job.Status = ProjectCopyJobStatuses.Pending;
                    job.ExecutionToken = null;
                    job.FilesCopied = 0;
                    job.BytesCopied = 0;
                    job.Error = null;
                    job.UpdatedAt = now;
                    await db.SaveChangesAsync(timeout.Token);
                    mayCleanup = true;
                }
                await tx.CommitAsync(timeout.Token);
            }
            if (mayCleanup)
                CleanupExecutionDirectory(unknown.JobId, unknown.ExecutionToken, timeout.Token);
            return true;
        }
        catch { return false; }
    }

    private static string SafeError(Exception error) => error is ApiException api
        ? api.Message.Length <= 255 ? api.Message : api.Message[..255]
        : "项目复制失败，请稍后重试或联系管理员";

    private static void InsertActivities(YfDbContext db, ulong sourceProjectId, ulong targetProjectId,
        ulong copyId, ulong actorId, string actorName, int fileCount, DateTime createdAt)
    {
        var summary = $"复制了 {fileCount} 个文件";
        db.ProjectActivities.AddRange(
            new ProjectActivity
            {
                ProjectId = sourceProjectId, ActivityType = "PROJECT", Action = "COPY", ActorId = actorId,
                ActorName = actorName, OccurredAt = createdAt, Title = "复制项目", Summary = summary,
                TargetId = sourceProjectId, SourceKey = $"project-copy:{copyId}:source",
            },
            new ProjectActivity
            {
                ProjectId = targetProjectId, ActivityType = "PROJECT", Action = "COPY", ActorId = actorId,
                ActorName = actorName, OccurredAt = createdAt, Title = "由复制创建", Summary = summary,
                TargetId = targetProjectId, SourceKey = $"project-copy:{copyId}:target",
            });
    }

    private static async Task<DateTime> DatabaseUtcNowAsync(YfDbContext db, CancellationToken ct) =>
        await DbClock.UtcNowAsync(db, ct, 3);

    private static DateOnly? ToDateOnly(DateTime? value) => value is null ? null : DateOnly.FromDateTime(value.Value);

    private static object Snapshot(ProjectRow project) => new { id = project.Id, project.Name, project.Status,
        project.SupplierId, project.WorkOrderNos, project.MachineModel, project.RobotPartId,
        project.ResponsibleUserId, project.SectionId, project.PriorityId, project.ExpectedCompletionDate };

    private static ProjectCopyHistoryItem HistoryItem(CopyHistoryRow row, ulong projectId, string name) => new(row.CopyId,
        projectId, name, row.FileCount, row.TotalBytes, row.CopiedByName, ProjectJson.Utc(row.CreatedAt));

    private static async Task<bool> CanViewAsync(MySqlConnection conn, MySqlTransaction tx, CurrentUser actor,
        ulong projectId, CancellationToken ct)
    {
        try { await ProjectAccessService.RequireViewForValidatedActorAsync(conn, tx, actor, projectId, false, ct); return true; }
        catch (ApiException error) when (error.Status is 403 or 404) { return false; }
    }

    private async Task PublishCommittedAsync(ulong projectId)
    {
        try { await realtime.PublishAsync(projectId, RealtimeChangeKinds.Activity, CancellationToken.None); }
        catch (Exception error)
        {
            if (logger is not null) LogRealtimePublishFailure(logger, projectId, error);
        }
    }

    private sealed record ProjectCopySnapshot(ProjectRow Project, CopyGroupRow Group, CopyFileRow[] Files);
    private sealed class CopyGroupRow
    {
        public ulong Id { get; init; }
        public ulong SupplierId { get; init; }
        public string Status { get; init; } = string.Empty;
        public string[] WorkOrderNos { get; set; } = [];
        public string? MachineModel { get; init; }
        public ulong? RobotPartId { get; init; }
        public string? LegacyRobotModelName { get; init; }
        public ulong? ResponsibleUserId { get; init; }
        public ulong? SectionId { get; init; }
        public ulong? PriorityId { get; init; }
        public DateTime? ExpectedCompletionDate { get; init; }
        public DateTime UpdatedAt { get; init; }
    }

    private sealed record PreparedCopy(CopyFileRow Source, string StoredName, FileBlobStore.PreparedBlob Blob);
    private sealed record CopyFileRow(ulong Id, ulong UploaderId, string Direction, string OriginalName, string Ext,
        ulong SizeBytes, string? MimeType, string? Sha256, string StoragePath, string Status, ulong? BlobId);
    private sealed class CopyHistoryRow
    {
        public ulong CopyId { get; init; }
        public ulong SourceProjectId { get; init; }
        public string SourceName { get; init; } = string.Empty;
        public ulong SourceSupplierId { get; init; }
        public ulong? SourceResponsibleUserId { get; init; }
        public ulong SourceGroupCreatorId { get; init; }
        public ulong TargetProjectId { get; init; }
        public string TargetName { get; init; } = string.Empty;
        public ulong TargetSupplierId { get; init; }
        public ulong? TargetResponsibleUserId { get; init; }
        public ulong TargetGroupCreatorId { get; init; }
        public string CopiedByName { get; init; } = string.Empty;
        public ulong FileCount { get; init; }
        public ulong TotalBytes { get; init; }
        public DateTime CreatedAt { get; init; }
    }

    private sealed record ProjectCopyIds(ulong SourceProjectId, ulong TargetProjectId);
    private sealed class ProjectCopyCommitOutcomeUnknownException(
        ulong jobId, string executionToken, ulong workerEpoch,
        ulong targetProjectId, ulong copyId, Exception inner)
        : Exception("Project copy commit outcome is unknown.", inner)
    {
        internal ulong JobId { get; } = jobId;
        internal string ExecutionToken { get; } = executionToken;
        internal ulong WorkerEpoch { get; } = workerEpoch;
        internal ulong TargetProjectId { get; } = targetProjectId;
        internal ulong CopyId { get; } = copyId;
    }
    private sealed class FileCopyHistoryRow
    {
        public ulong SourceFileId { get; init; }
        public string SourceFileName { get; init; } = string.Empty;
        public ulong TargetFileId { get; init; }
        public string TargetFileName { get; init; } = string.Empty;
        public string? SourceStatus { get; init; }
        public string? TargetStatus { get; init; }
    }
}
