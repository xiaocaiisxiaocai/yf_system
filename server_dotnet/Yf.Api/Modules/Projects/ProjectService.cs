using System.Globalization;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Yf.Api.Infrastructure;
using Yf.Api.Infrastructure.Entities;
using Yf.Api.Modules.SystemManagement;

namespace Yf.Api.Modules.Projects;

/// <summary>
/// Projects are a single level: each project carries its public data (supplier, work orders, machine and Robot
/// data, responsible user, section, priority, due date) together with its files, messages and material review.
/// Every internal account sees every project; only the responsible user changes one. See 项目协作契约.
/// </summary>
internal sealed partial class ProjectService(
    AuditService audit,
    AppOptions options,
    IProjectRealtimePublisher? realtime = null,
    ILogger<ProjectService>? logger = null)
{
    private static readonly Action<ILogger, ulong, Exception?> LogRealtimePublishFailure =
        LoggerMessage.Define<ulong>(LogLevel.Warning, new EventId(1, "ProjectRealtimePublishFailed"),
            "Realtime publish failed after project commit for project {ProjectId}");

    internal const string ProjectChangedMessage = "项目已被他人修改，请刷新后重试";
    internal const string NameExistsMessage = "项目名称已存在";

    internal async Task<PageResponse<ProjectResponse>> ListAsync(
        MySqlConnection conn,
        CurrentUser actor,
        ulong page,
        ulong pageSize,
        string? keyword,
        string? status,
        ulong? supplierId,
        string? materialState,
        bool overdue,
        bool mine,
        CancellationToken ct)
    {
        var (actualPage, size) = ProjectJson.ClampPage(page, pageSize);
        status = NormalizeStatus(status);
        materialState = NormalizeMaterialState(materialState);
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        var current = await AccessService.ReadActorAsync(conn, tx, actor, ct);
        await using var db = EfDb.Use(conn, tx);
        var query = mine
            ? await ProjectAccessService.InboxQueryAsync(db, current, ct)
            : await ProjectAccessService.VisibleQueryAsync(db, current, ct);
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            // One box finds a project by any identifying text shown in the list: name, work order number,
            // machine model, Robot part number / model (or the legacy model name) and Robot owner.
            var pattern = QueryValues.ContainsPattern(keyword.Trim());
            query = query.Where(project => EF.Functions.Like(project.Name, pattern, QueryValues.LikeEscape)
                || (project.MachineModel != null && EF.Functions.Like(project.MachineModel, pattern, QueryValues.LikeEscape))
                || (project.RobotOwnerName != null && EF.Functions.Like(project.RobotOwnerName, pattern, QueryValues.LikeEscape))
                || (project.LegacyRobotModelName != null && EF.Functions.Like(project.LegacyRobotModelName, pattern, QueryValues.LikeEscape))
                || db.ProjectWorkOrders.Any(order => order.ProjectId == project.Id
                    && EF.Functions.Like(order.WorkOrderNo, pattern, QueryValues.LikeEscape))
                || db.RobotParts.Any(part => part.Id == project.RobotPartId
                    && (EF.Functions.Like(part.PartNumber, pattern, QueryValues.LikeEscape)
                        || EF.Functions.Like(part.Model, pattern, QueryValues.LikeEscape))));
        }
        if (status is not null) query = query.Where(project => project.Status == status);
        if (supplierId is not null) query = query.Where(project => project.SupplierId == supplierId.Value);
        if (materialState is not null)
        {
            var matching = ProjectQueries.InProgressMaterial(db).Where(row => row.State == materialState)
                .Select(row => row.ProjectId);
            query = query.Where(project => matching.Contains(project.Id));
        }
        if (overdue)
        {
            var today = BusinessCalendar.Today(await DbClock.UtcNowAsync(db, ct, 0));
            query = query.Where(project => project.Status == ProjectStatuses.InProgress
                && project.ExpectedCompletionDate != null && project.ExpectedCompletionDate < today);
        }
        var total = (ulong)await query.LongCountAsync(ct);
        var ids = query.Select(project => project.Id);
        var rows = await ProjectQueries.Rows(db).Where(row => ids.Contains(row.Id))
            .OrderByDescending(row => row.Id)
            .Page((actualPage - 1) * size, size).ToArrayAsync(ct);
        await LoadExtrasAsync(db, rows, current, ct);
        var deletionScope = await ProjectDeletionAuthorization.LoadScopeAsync(conn, tx, current, ct);
        await tx.CommitAsync(ct);
        return ProjectJson.Page(rows.Select(row => ProjectJson.Project(
            row, current.IsInternal, current.Id, deletionScope.CanDelete(row.SectionId))).ToArray(),
            total, actualPage, size);
    }

    internal async Task<ProjectResponse> DetailAsync(MySqlConnection conn, CurrentUser actor, ulong projectId, CancellationToken ct)
    {
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        var current = await AccessService.ReadActorAsync(conn, tx, actor, ct);
        await ProjectAccessService.RequireViewForValidatedActorAsync(conn, tx, current, projectId, false, ct);
        await using var db = EfDb.Use(conn, tx);
        var project = await LoadProjectAsync(conn, tx, projectId, false, ct);
        await LoadExtrasAsync(db, [project], current, ct);
        var sourceCopy = await (
            from copy in db.ProjectCopies
            join source in db.Projects on copy.SourceProjectId equals source.Id
            where copy.TargetProjectId == projectId
            select new ProjectCopySourceRow { ProjectId = source.Id, Name = source.Name })
            .SingleOrDefaultAsync(ct);
        ProjectCopySourceRef? copySource = null;
        if (sourceCopy is not null)
        {
            try
            {
                await ProjectAccessService.RequireViewForValidatedActorAsync(
                    conn, tx, current, sourceCopy.ProjectId, false, ct);
                copySource = new ProjectCopySourceRef(sourceCopy.ProjectId, sourceCopy.Name);
            }
            catch (ApiException error) when (error.Status is 403 or 404)
            {
                copySource = null;
            }
        }
        var deletionScope = await ProjectDeletionAuthorization.LoadScopeAsync(conn, tx, current, ct);
        var result = ProjectJson.Project(project, current.IsInternal, current.Id, deletionScope.CanDelete(project.SectionId)) with
        {
            CopySource = copySource,
        };
        await tx.CommitAsync(ct);
        return result;
    }

    /// <summary>
    /// Creates a project and starts it in the same transaction: the supplier prerequisites of START apply, and a
    /// failed prerequisite leaves no row behind.
    /// </summary>
    internal async Task<ProjectResponse> CreateAsync(
        MySqlConnection conn,
        CurrentUser actor,
        ProjectUpsertRequest request,
        string? ip,
        CancellationToken ct)
    {
        if (!actor.IsInternal) throw ApiException.Forbidden();
        var name = ValidateNameForCreate(request.Name);
        ValidateDescription(request.Description);
        var metadata = NormalizeMetadata(request, true);
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        await AccessService.LockBusinessAsync(conn, tx, ct);
        var current = await AccessService.RecheckActorAsync(conn, tx, actor, ct);
        await AccessService.RequirePermissionAsync(conn, tx, current, "project:list", ct);
        await AccessService.RequirePermissionAsync(conn, tx, current, "project:create", ct);
        await using var db = EfDb.Use(conn, tx);
        await EnsureSupplierAsync(db, request.SupplierId, ct);
        await EnsureNameUniqueAsync(conn, tx, name, null, ct);
        metadata = metadata with
        {
            ResponsibleUserId = current.Id,
            SectionId = await ResolveActorSectionAsync(db, current.Id, ct),
        };
        metadata = await ValidateMetadataAsync(conn, tx, metadata, null, request.SupplierId, ct);
        metadata = metadata with
        {
            RobotOwnerName = await RobotOwners.ResolveSnapshotAsync(
                db, request.SupplierId, metadata.RobotOwnerContactIds, [], ct),
        };
        await EnsureResponsibleUserCanActAsync(conn, tx, current.Id, ct);
        await EnsureSupplierCanCollaborateAsync(conn, tx, request.SupplierId, ct);

        var now = await DatabaseUtcNowAsync(db, ct);
        var project = new Project
        {
            Name = name,
            Description = request.Description,
            SupplierId = request.SupplierId,
            Status = ProjectStatuses.InProgress,
            CreatedBy = current.Id,
            MachineModel = metadata.MachineModel,
            RobotPartId = metadata.RobotPartId,
            ResponsibleUserId = metadata.ResponsibleUserId,
            RobotOwnerName = metadata.RobotOwnerName,
            SectionId = metadata.SectionId,
            PriorityId = metadata.PriorityId,
            RobotTypeId = metadata.RobotTypeId,
            ExpectedCompletionDate = ToDateOnly(metadata.ExpectedCompletionDate),
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Projects.Add(project);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException error) when (error.InnerException is MySqlException { Number: 1062 })
        { throw ApiException.Conflict(NameExistsMessage); }

        db.ProjectWorkOrders.AddRange(metadata.WorkOrderNos.Select((value, index) =>
            new ProjectWorkOrder { ProjectId = project.Id, WorkOrderNo = value, SortNo = index, CreatedAt = now }));
        var statusLog = new ProjectStatusLog
        {
            ProjectId = project.Id,
            FromStatus = null,
            ToStatus = ProjectStatuses.InProgress,
            Action = "START",
            OperatorId = current.Id,
            CreatedAt = now,
        };
        db.ProjectStatusLogs.Add(statusLog);
        await db.SaveChangesAsync(ct);
        await RobotOwners.ReplaceAsync(db, project.Id, metadata.RobotOwnerContactIds, ct);

        await audit.WriteBatchAsync(conn, tx, current.Id, [
            new AuditWrite("PROJECT_CREATE", "project", project.Id, new
            {
                name,
                request.SupplierId,
                metadata.WorkOrderNos,
                metadata.MachineModel,
                metadata.RobotPartId,
                metadata.ResponsibleUserId,
                metadata.RobotOwnerName,
                metadata.RobotOwnerContactIds,
                metadata.SectionId,
                metadata.PriorityId,
                metadata.RobotTypeId,
                metadata.ExpectedCompletionDate,
            }),
            new AuditWrite("PROJECT_START", "project", project.Id, new
            {
                from = (string?)null,
                to = ProjectStatuses.InProgress,
                action = "START",
                reason = (string?)null,
                statusLogId = statusLog.Id,
            }),
        ], ip, ct);
        var row = await LoadProjectAsync(conn, tx, project.Id, false, ct);
        await ProjectNotificationService.EnqueueStatusChangeAsync(
            conn, tx, row, "START", null, current, options.WebBaseUrl, audit, ct);
        var result = await ResultAsync(conn, tx, current, project.Id, ct);
        await tx.CommitAsync(ct);
        return result;
    }

    internal async Task<ProjectResponse> UpdateAsync(
        MySqlConnection conn,
        CurrentUser actor,
        ulong projectId,
        ProjectUpsertRequest request,
        string? ip,
        CancellationToken ct)
    {
        if (!actor.IsInternal) throw ApiException.Forbidden();
        var name = ValidateNameForUpdate(request.Name);
        ValidateDescription(request.Description);
        var metadata = NormalizeMetadata(request, false);
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        await AccessService.LockBusinessAsync(conn, tx, ct);
        var current = await AccessService.RecheckActorAsync(conn, tx, actor, ct);
        await AccessService.RequirePermissionAsync(conn, tx, current, "project:update", ct);
        // Ownership is checked before the row lock and again on the locked values.
        await ProjectAccessService.RequireViewForValidatedActorAsync(conn, tx, current, projectId, true, ct, ownerOnly: true);
        if (request.ExpectedUpdatedAt is not { } expectedUpdatedAt)
            throw ApiException.BadRequest("expectedUpdatedAt 必须为当前项目的 updatedAt");
        await using var db = EfDb.Use(conn, tx);
        var before = await LoadProjectAsync(conn, tx, projectId, false, ct);
        await LoadExtrasAsync(db, [before], current, ct, loadUnread: false);
        // Optimistic concurrency against the row locked above: an editor that loaded an older version must
        // refresh instead of silently overwriting someone else's change.
        if (ProjectJson.Utc(before.UpdatedAt) != ProjectJson.Utc(expectedUpdatedAt))
            throw ApiException.VersionConflict(ProjectChangedMessage);
        if (before.Status != ProjectStatuses.InProgress)
            throw ApiException.Conflict("项目已结束，不能修改资料");
        if (before.SupplierId != request.SupplierId)
            throw ApiException.BadRequest("项目创建后不可更换供应商");
        await EnsureNameUniqueAsync(conn, tx, name, projectId, ct);
        metadata = await ValidateMetadataAsync(conn, tx, metadata, before, before.SupplierId, ct);
        metadata = metadata with
        {
            ResponsibleUserId = before.ResponsibleUserId,
            SectionId = before.SectionId,
            RobotOwnerName = await RobotOwners.ResolveSnapshotAsync(
                db, before.SupplierId, metadata.RobotOwnerContactIds, before.RobotOwnerContactIds, ct),
        };
        var robotOwnersChanged = !before.RobotOwnerContactIds.SequenceEqual(metadata.RobotOwnerContactIds);
        var workOrdersChanged = !before.WorkOrderNos.SequenceEqual(metadata.WorkOrderNos, StringComparer.Ordinal);
        var expectedCompletionDate = DateValue(metadata.ExpectedCompletionDate);
        var changes = AuditChange.OnlyChanged(
            new("name", "项目名称", before.Name, name),
            new("description", "项目说明", before.Description, request.Description),
            new("workOrderNos", "工令号", before.WorkOrderNos, metadata.WorkOrderNos),
            new("machineModel", "机型", before.MachineModel, metadata.MachineModel),
            new("robotPartId", "Robot 料号", before.RobotPartId, metadata.RobotPartId),
            new("priorityId", "优先级", before.PriorityId, metadata.PriorityId),
            new("robotTypeId", "Robot 类型", before.RobotTypeId, metadata.RobotTypeId),
            new("robotOwnerName", "Robot 负责人", before.RobotOwnerName, metadata.RobotOwnerName),
            new("robotOwnerContactIds", "Robot 负责人联络人", before.RobotOwnerContactIds, metadata.RobotOwnerContactIds),
            new("expectedCompletionDate", "预计完成日期", DateValue(before.ExpectedCompletionDate), expectedCompletionDate));
        if (changes.Length == 0)
        {
            var unchanged = await ResultAsync(conn, tx, current, projectId, ct);
            await tx.CommitAsync(ct);
            return unchanged;
        }

        var now = await DatabaseUtcNowAsync(db, ct);
        try
        {
            await db.Projects.Where(item => item.Id == projectId).ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Name, name)
                .SetProperty(item => item.Description, request.Description)
                .SetProperty(item => item.MachineModel, metadata.MachineModel)
                .SetProperty(item => item.RobotPartId, metadata.RobotPartId)
                .SetProperty(item => item.LegacyRobotModelName,
                    item => metadata.RobotPartId != null ? (string?)null : item.LegacyRobotModelName)
                .SetProperty(item => item.PriorityId, metadata.PriorityId)
                .SetProperty(item => item.RobotTypeId, metadata.RobotTypeId)
                .SetProperty(item => item.RobotOwnerName, metadata.RobotOwnerName)
                .SetProperty(item => item.ExpectedCompletionDate, ToDateOnly(metadata.ExpectedCompletionDate))
                .SetProperty(item => item.UpdatedAt, now), ct);
        }
        catch (MySqlException error) when (error.Number == 1062)
        { throw ApiException.Conflict(NameExistsMessage); }
        catch (DbUpdateException error) when (error.InnerException is MySqlException { Number: 1062 })
        { throw ApiException.Conflict(NameExistsMessage); }
        if (robotOwnersChanged) await RobotOwners.ReplaceAsync(db, projectId, metadata.RobotOwnerContactIds, ct);
        if (workOrdersChanged) await ReplaceWorkOrdersAsync(conn, tx, projectId, metadata.WorkOrderNos, ct);
        await audit.WriteAsync(conn, tx, current.Id, "PROJECT_UPDATE", "project", projectId,
            new { name, changes }, ip, ct);
        var result = await ResultAsync(conn, tx, current, projectId, ct);
        await tx.CommitAsync(ct);
        // Name, description and data changes write no activity a client listens to, so tell viewers directly.
        await PublishAfterCommitAsync(projectId);
        return result;
    }

    /// <summary>
    /// Hands the project to another eligible internal account. The current responsible user, system administrators
    /// and the leaders of the project's section or its parent organizations may transfer (all need project:transfer).
    /// </summary>
    internal async Task<ProjectResponse> TransferAsync(
        MySqlConnection conn,
        CurrentUser actor,
        ulong projectId,
        ProjectTransferRequest request,
        string? ip,
        CancellationToken ct)
    {
        if (!actor.IsInternal) throw ApiException.Forbidden();
        if (request.ResponsibleUserId is not ulong targetId || targetId == 0)
            throw ApiException.BadRequest("请选择新的负责人");
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        await AccessService.LockBusinessAsync(conn, tx, ct);
        var current = await AccessService.RecheckActorAsync(conn, tx, actor, ct);
        await AccessService.RequirePermissionAsync(conn, tx, current, "project:transfer", ct);
        var scope = await ProjectDeletionAuthorization.LoadScopeAsync(conn, tx, current, ct);
        // Authorized on the unlocked read first, so an unauthorized caller never waits for the row lock.
        RequireTransferAuthority(current, scope,
            await ProjectAccessService.RequireViewForValidatedActorAsync(conn, tx, current, projectId, false, ct));
        var access = await ProjectAccessService.RequireViewForValidatedActorAsync(conn, tx, current, projectId, true, ct);
        RequireTransferAuthority(current, scope, access);
        // Optional guard against the locked row: a caller that decided from a stale owner must refresh.
        if (request.ExpectedResponsibleUserId is ulong expectedOwnerId && access.ResponsibleUserId != expectedOwnerId)
            throw ApiException.VersionConflict("项目负责人已被他人变更，请刷新后重试");
        if (access.ResponsibleUserId == targetId) throw ApiException.BadRequest("新负责人与当前负责人相同");
        await using var db = EfDb.Use(conn, tx);
        var owner = await EligibleOwners(db).Where(row => row.Id == targetId).SingleOrDefaultAsync(ct)
            ?? throw ApiException.BadRequest("新负责人必须是启用且拥有项目列表权限的公司内部账号");
        var before = await LoadProjectAsync(conn, tx, projectId, false, ct);
        var now = await DatabaseUtcNowAsync(db, ct);
        await db.Projects.Where(item => item.Id == projectId).ExecuteUpdateAsync(setters => setters
            .SetProperty(item => item.ResponsibleUserId, owner.Id)
            .SetProperty(item => item.SectionId, owner.SectionId)
            .SetProperty(item => item.UpdatedAt, now), ct);
        var changes = AuditChange.OnlyChanged(
            new AuditChange("responsibleUserId", "负责人",
                before.ResponsibleUserId is null ? null : new
                {
                    id = before.ResponsibleUserId, employeeNo = before.ResponsibleUserEmployeeNo, name = before.ResponsibleUserName,
                },
                new { id = owner.Id, employeeNo = owner.EmployeeNo, name = owner.RealName }),
            new AuditChange("sectionId", "课别",
                before.SectionId is null ? null : new { id = before.SectionId, name = before.SectionName },
                owner.SectionId is null ? null : new { id = owner.SectionId, name = owner.SectionName }));
        await audit.WriteAsync(conn, tx, current.Id, "PROJECT_UPDATE", "project", projectId,
            new { name = before.Name, changes }, ip, ct);
        var result = await ResultAsync(conn, tx, current, projectId, ct);
        await tx.CommitAsync(ct);
        await PublishAfterCommitAsync(projectId);
        return result;
    }

    internal async Task<ProjectSummaryResponse> SummaryAsync(MySqlConnection conn, CurrentUser actor, ulong projectId, CancellationToken ct)
    {
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        var current = await AccessService.ReadActorAsync(conn, tx, actor, ct);
        var access = await ProjectAccessService.RequireViewForValidatedActorAsync(conn, tx, current, projectId, false, ct);
        var unread = FollowsProject(current, access.ResponsibleUserId)
            ? await MessageService.UnreadCountAsync(conn, tx, current.Id, projectId, ct)
            : 0;
        var activityRevision = await ProjectActivityService.RevisionAsync(conn, tx, projectId, ct);
        var result = new ProjectSummaryResponse(unread, activityRevision);
        await tx.CommitAsync(ct);
        return result;
    }

    internal async Task<List<SupplierOption>> SupplierOptionsAsync(MySqlConnection conn, CurrentUser actor, CancellationToken ct)
    {
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        var current = await AccessService.ReadActorAsync(conn, tx, actor, ct);
        AccessService.RequireInternal(current);
        await AccessService.RequirePermissionAsync(conn, tx, current, "project:list", ct);
        await using var db = EfDb.Use(conn, tx);
        var rows = await db.Suppliers.Where(supplier => supplier.Status == AccountStatuses.Active)
            .OrderBy(supplier => supplier.Id)
            .Select(supplier => new SupplierOption(supplier.Id, supplier.Name))
            .ToListAsync(ct);
        await tx.CommitAsync(ct);
        return rows;
    }

    internal async Task<List<ProjectOwnerOption>> ProjectOwnerOptionsAsync(MySqlConnection conn, CurrentUser actor, CancellationToken ct)
    {
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        var current = await AccessService.ReadActorAsync(conn, tx, actor, ct);
        AccessService.RequireInternal(current);
        await AccessService.RequirePermissionAsync(conn, tx, current, "project:list", ct);
        await AccessService.RequirePermissionAsync(conn, tx, current, "project:transfer", ct);
        await using var db = EfDb.Use(conn, tx);
        var rows = await EligibleOwners(db)
            .OrderBy(owner => owner.RealName).ThenBy(owner => owner.Id)
            .Select(owner => new ProjectOwnerOption(owner.Id, owner.EmployeeNo, owner.RealName, owner.SectionId, owner.SectionName))
            .ToListAsync(ct);
        await tx.CommitAsync(ct);
        return rows;
    }

    internal async Task DeleteAsync(
        MySqlConnection conn,
        CurrentUser actor,
        ulong projectId,
        string? ip,
        CancellationToken ct)
    {
        if (!actor.IsInternal)
        {
            throw ApiException.Forbidden();
        }
        // Scope and deletion authority are checked before any blob lookup or lock, so an unauthorized caller never waits.
        await using (var precheck = await AppDb.BeginTransactionAsync(conn, ct))
        {
            var reader = await AccessService.ReadActorAsync(conn, precheck, actor, ct);
            var visible = await ProjectAccessService.RequireViewForValidatedActorAsync(conn, precheck, reader, projectId, false, ct);
            await ProjectDeletionAuthorization.RequireAsync(conn, precheck, reader, visible.SectionId, ct);
            await precheck.CommitAsync(ct);
        }
        // Deleting a project deletes its files, whose blobs may be shared with other files (copies, re-uploads
        // of the same content). Like copy and upload, take the SHA named locks before the transaction, so
        // releasing the last reference cannot race a new reference.
        string[] blobShas;
        await using (var lookup = EfDb.Use(conn))
            blobShas = await (from file in lookup.Files
                join blob in lookup.FileBlobs on file.BlobId equals blob.Id
                where file.ProjectId == projectId
                select blob.Sha256).Distinct().ToArrayAsync(ct);
        await using var blobLeases = await Files.FileBlobStore.AcquireAsync(conn, blobShas, ct);
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        await AccessService.LockBusinessAsync(conn, tx, ct);
        var current = await AccessService.RecheckActorAsync(conn, tx, actor, ct);
        await ProjectAccessService.RequireViewForValidatedActorAsync(conn, tx, current, projectId, true, ct);
        var project = await LoadProjectAsync(conn, tx, projectId, false, ct);
        await ProjectDeletionAuthorization.RequireAsync(conn, tx, current, project.SectionId, ct);
        ProjectWorkflowRules.EnsureDeletable(project.Status);
        await using var db = EfDb.Use(conn, tx);
        if (await db.ProjectCopyJobs.AnyAsync(job => job.SourceProjectId == projectId
                && (job.Status == ProjectCopyJobStatuses.Pending || job.Status == ProjectCopyJobStatuses.Running), ct))
            throw ApiException.Conflict("项目仍有进行中的复制任务，请等待任务完成后再删除");
        // A source keeps its copy history; only the derived copy (the target) may be removed.
        if (await db.ProjectCopies.AnyAsync(copy => copy.SourceProjectId == projectId, ct))
            throw ApiException.Conflict("该项目已被复制出副本，请先删除副本");
        var copyRecord = await db.ProjectCopies.AsNoTracking()
            .SingleOrDefaultAsync(copy => copy.TargetProjectId == projectId, ct);
        // Deleting a project deletes everything in it: files, messages (their images and read receipts
        // cascade), upload records, activity and status history. Only an upload still in flight blocks, as it
        // does for terminate; uploads cannot start afterwards because they require an in-progress project.
        if (await CountActiveUploadsAsync(conn, tx, projectId, supplierOnly: false, ct) != 0)
            throw ApiException.Conflict("项目仍有进行中的上传，请等待上传完成或取消后再删除");
        var releasedBlobIds = await db.Files.Where(file => file.ProjectId == projectId && file.BlobId != null)
            .Select(file => file.BlobId!.Value).Distinct().OrderBy(id => id).ToArrayAsync(ct);
        var leased = blobShas.ToHashSet(StringComparer.Ordinal);
        var currentShas = await db.FileBlobs.Where(blob => releasedBlobIds.Contains(blob.Id))
            .Select(blob => blob.Sha256).ToArrayAsync(ct);
        if (currentShas.Any(sha => !leased.Contains(sha)))
            throw ApiException.Conflict("文件内容引用已变化，请刷新后重试");
        var fileCount = await db.Files.LongCountAsync(file => file.ProjectId == projectId, ct);
        var messageCount = await db.Messages.LongCountAsync(message => message.ProjectId == projectId, ct);
        if (copyRecord is not null)
        {
            // file_copy_refs cascade with the copy row; the jobs only point at the result for navigation.
            await db.ProjectCopyJobs.Where(job => job.ResultProjectId == projectId || job.ResultCopyId == copyRecord.Id)
                .ExecuteDeleteAsync(ct);
            await db.ProjectCopies.Where(copy => copy.Id == copyRecord.Id).ExecuteDeleteAsync(ct);
        }
        // Chunk directories of finished or abandoned sessions and message image files left on disk have no row
        // any more; FilesMaintenanceService removes them as orphans after its grace period.
        await db.UploadSessions.Where(upload => upload.ProjectId == projectId).ExecuteDeleteAsync(ct);
        await db.Messages.Where(message => message.ProjectId == projectId).ExecuteDeleteAsync(ct);
        await db.Files.Where(file => file.ProjectId == projectId).ExecuteDeleteAsync(ct);
        if (releasedBlobIds.Length > 0)
        {
            var now = await DbClock.UtcNowAsync(db, ct);
            foreach (var blobId in releasedBlobIds)
            {
                var blob = await db.FileBlobs
                    .FromSqlInterpolated($"SELECT * FROM file_blobs WHERE id={blobId} FOR UPDATE")
                    .SingleAsync(ct);
                if (await db.Files.AnyAsync(file => file.BlobId == blobId, ct)) continue;
                // Last reference released: FilesMaintenanceService removes the content in its next cycle.
                blob.State = FileBlobStates.GarbageCollectionPending;
                blob.GarbageCollectionStartedAt = now;
            }
            await db.SaveChangesAsync(ct);
        }
        var deletionAudience = (await ProjectRealtimeAuthorizer.SnapshotAudiencesAsync(db, [projectId], ct)).SingleOrDefault();
        await db.EmailOutbox.Where(mail => mail.ProjectId == projectId
                && (mail.Status == MailStatuses.Pending || mail.Status == MailStatuses.Sending))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(mail => mail.Status, MailStatuses.Cancelled)
                .SetProperty(mail => mail.NextAttemptAt, (DateTime?)null)
                .SetProperty(mail => mail.LastError, "项目已删除，邮件已取消"), ct);
        // Preserve delivery history while releasing the restrictive project foreign key.
        await db.EmailOutbox.Where(mail => mail.ProjectId == projectId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(mail => mail.ProjectId, (ulong?)null), ct);
        await db.ProjectStatusLogs.Where(log => log.ProjectId == projectId).ExecuteDeleteAsync(ct);
        object auditDetails = copyRecord is null
            ? new { name = project.Name, deletedFileCount = fileCount, deletedMessageCount = messageCount }
            : new
            {
                name = project.Name,
                deletedFileCount = fileCount,
                deletedMessageCount = messageCount,
                copiedFromProjectId = copyRecord.SourceProjectId,
                copiedFromProjectName = copyRecord.SourceProjectName,
                copiedFileCount = copyRecord.FileCount,
            };
        await audit.WriteAsync(conn, tx, current.Id, "PROJECT_DELETE", "project", projectId, auditDetails, ip, ct);
        await db.ProjectActivities.Where(activity => activity.ProjectId == projectId).ExecuteDeleteAsync(ct);
        var deleted = await db.Projects.Where(item => item.Id == projectId).ExecuteDeleteAsync(ct);
        if (deleted != 1)
        {
            throw ApiException.Conflict("项目已被删除，请刷新后重试");
        }
        await tx.CommitAsync(ct);
        if (realtime is not null && deletionAudience is not null)
        {
            try { await realtime.PublishAsync(deletionAudience, RealtimeChangeKinds.Project, CancellationToken.None); }
            catch (Exception error)
            {
                if (logger is not null) LogRealtimePublishFailure(logger, projectId, error);
            }
        }
    }

    private static void RequireTransferAuthority(CurrentUser current, ProjectDeletionScope scope, ProjectAccess access)
    {
        if (access.ResponsibleUserId != current.Id && !scope.CanDelete(access.SectionId))
            throw ApiException.Forbidden("仅项目负责人、所属组织主管或系统管理员可以变更负责人");
    }

    /// <summary>Pushes a coalesced <c>project</c> signal after commit; a push failure never undoes the commit.</summary>
    private async Task PublishAfterCommitAsync(ulong projectId)
    {
        if (realtime is null) return;
        try { await realtime.PublishAsync(projectId, RealtimeChangeKinds.Project, CancellationToken.None); }
        catch (Exception error)
        {
            if (logger is not null) LogRealtimePublishFailure(logger, projectId, error);
        }
    }

    /// <summary>An internal account follows (unread counts, notifications) only the projects it is responsible for.</summary>
    private static bool FollowsProject(CurrentUser current, ulong? responsibleUserId) =>
        !current.IsInternal || responsibleUserId == current.Id;

    /// <summary>The response the caller of a write sees, with its own manage and delete flags.</summary>
    private static async Task<ProjectResponse> ResultAsync(
        MySqlConnection conn, MySqlTransaction tx, CurrentUser current, ulong projectId, CancellationToken ct)
    {
        await using var db = EfDb.Use(conn, tx);
        var row = await LoadProjectAsync(conn, tx, projectId, false, ct);
        await LoadExtrasAsync(db, [row], current, ct, loadUnread: false);
        var deletionScope = await ProjectDeletionAuthorization.LoadScopeAsync(conn, tx, current, ct);
        return ProjectJson.Project(row, current.IsInternal, current.Id, deletionScope.CanDelete(row.SectionId));
    }

    /// <summary>Robot owners and, for projects the caller follows, unread messages within the unread window.</summary>
    private static async Task LoadExtrasAsync(
        YfDbContext db, IReadOnlyCollection<ProjectRow> projects, CurrentUser current, CancellationToken ct,
        bool loadUnread = true)
    {
        if (projects.Count == 0) return;
        var ids = projects.Select(project => project.Id).ToArray();
        var workOrders = (await db.ProjectWorkOrders.Where(order => Enumerable.Contains(ids, order.ProjectId))
                .OrderBy(order => order.ProjectId).ThenBy(order => order.SortNo).ThenBy(order => order.Id)
                .Select(order => new ProjectWorkOrderRow { ProjectId = order.ProjectId, WorkOrderNo = order.WorkOrderNo })
                .ToArrayAsync(ct))
            .GroupBy(row => row.ProjectId)
            .ToDictionary(group => group.Key, group => group.Select(row => row.WorkOrderNo).ToArray());
        var owners = await RobotOwners.LoadAsync(db, ids, ct);
        var unread = new Dictionary<ulong, ulong>();
        var followed = projects.Where(project => FollowsProject(current, project.ResponsibleUserId))
            .Select(project => project.Id).ToArray();
        if (loadUnread && followed.Length > 0)
        {
            var userId = current.Id;
            var cutoff = await UnreadWindow.CutoffAsync(db, ct);
            unread = (await db.Messages.Where(message => Enumerable.Contains(followed, message.ProjectId)
                        && message.Status == "NORMAL" && message.SenderId != userId && message.CreatedAt >= cutoff
                        && !db.MessageReads.Any(receipt => receipt.MessageId == message.Id && receipt.UserId == userId))
                    .GroupBy(message => message.ProjectId)
                    .Select(group => new { ProjectId = group.Key, Count = group.LongCount() })
                    .ToArrayAsync(ct))
                .ToDictionary(row => row.ProjectId, row => (ulong)row.Count);
        }
        foreach (var project in projects)
        {
            project.WorkOrderNos = workOrders.GetValueOrDefault(project.Id) ?? [];
            project.RobotOwnerContactIds = owners.GetValueOrDefault(project.Id) ?? [];
            project.UnreadMessages = unread.GetValueOrDefault(project.Id);
        }
    }

    private static async Task<ProjectRow> LoadProjectAsync(
        MySqlConnection conn,
        MySqlTransaction? tx,
        ulong projectId,
        bool forUpdate,
        CancellationToken ct)
    {
        await using var db = EfDb.Use(conn, tx);
        if (forUpdate && await db.Projects
                .FromSqlInterpolated($"SELECT * FROM projects WHERE id={projectId} FOR UPDATE")
                .AsNoTracking().SingleOrDefaultAsync(ct) is null)
            throw ApiException.NotFound();

        var row = await ProjectQueries.Rows(db).SingleOrDefaultAsync(item => item.Id == projectId, ct);
        if (row is null) throw ApiException.NotFound();
        row.WorkOrderNos = await db.ProjectWorkOrders.Where(order => order.ProjectId == projectId)
            .OrderBy(order => order.SortNo).ThenBy(order => order.Id)
            .Select(order => order.WorkOrderNo).ToArrayAsync(ct);
        return row;
    }

    private static async Task EnsureSupplierAsync(YfDbContext db, ulong supplierId, CancellationToken ct)
    {
        var status = await db.Suppliers.Where(supplier => supplier.Id == supplierId)
            .Select(supplier => supplier.Status).SingleOrDefaultAsync(ct);
        if (status is null) throw ApiException.BadRequest("供应商不存在");
        if (status != AccountStatuses.Active) throw ApiException.BadRequest("供应商已被禁用");
    }

    internal static async Task EnsureNameUniqueAsync(
        MySqlConnection conn,
        MySqlTransaction tx,
        string name,
        ulong? excludeId,
        CancellationToken ct)
    {
        await using var db = EfDb.Use(conn, tx);
        var exists = await db.Projects.AnyAsync(
            project => project.Name == name && (excludeId == null || project.Id != excludeId.Value), ct);
        if (exists)
        {
            throw ApiException.Conflict(NameExistsMessage);
        }
    }

    internal static string? NormalizeStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return null;
        var value = status.Trim();
        if (value is not (ProjectStatuses.InProgress or ProjectStatuses.Completed or ProjectStatuses.Terminated))
            throw ApiException.BadRequest("项目状态不正确");
        return value;
    }

    internal static string? NormalizeMaterialState(string? state)
    {
        if (string.IsNullOrWhiteSpace(state)) return null;
        var value = state.Trim();
        if (value is not (MaterialReviewStatuses.NoMaterial or MaterialReviewStatuses.Pending or MaterialReviewStatuses.Returned))
            throw ApiException.BadRequest("资料状态不正确");
        return value;
    }

    internal static string[] NormalizeWorkOrderNos(string?[]? values)
    {
        if (values is null) return [];
        var normalized = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            var item = (value ?? string.Empty).Trim();
            if (item.Length == 0) continue;
            if (RuneCount(item) > 128) throw ApiException.BadRequest("单个工令号不能超过 128 个字符");
            if (seen.Add(item)) normalized.Add(item);
        }
        if (normalized.Count > 50) throw ApiException.BadRequest("工令号不能超过 50 个");
        return normalized.ToArray();
    }

    internal static ProjectMetadataInput NormalizeMetadata(ProjectUpsertRequest request, bool requireRobotPart)
    {
        var workOrderNos = NormalizeWorkOrderNos(request.WorkOrderNos);
        if (workOrderNos.Length == 0)
            throw ApiException.BadRequest("请至少填写一个工令号");
        if (string.IsNullOrWhiteSpace(request.MachineModel)) throw ApiException.BadRequest("请填写机型");
        if (request.RobotTypeId is null or 0) throw ApiException.BadRequest("请选择 Robot 类型");
        if (requireRobotPart && request.RobotPartId is null or 0) throw ApiException.BadRequest("请选择 Robot 料号");
        if (request.PriorityId is null or 0) throw ApiException.BadRequest("请选择优先级");
        if (string.IsNullOrWhiteSpace(request.ExpectedCompletionDate)) throw ApiException.BadRequest("请选择需求完成时间");
        var machineModel = string.IsNullOrWhiteSpace(request.MachineModel) ? null : request.MachineModel.Trim();
        if (machineModel is not null && RuneCount(machineModel) > 128)
            throw ApiException.BadRequest("机型不能超过 128 个字符");
        var robotOwnerContactIds = RobotOwners.Normalize(request.RobotOwnerContactIds);
        DateTime? expectedCompletionDate = null;
        if (!string.IsNullOrWhiteSpace(request.ExpectedCompletionDate))
        {
            var raw = request.ExpectedCompletionDate.Trim();
            if (!DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var parsed)
                || parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) != raw)
                throw ApiException.BadRequest("需求完成时间必须为 yyyy-MM-dd 格式");
            expectedCompletionDate = parsed.ToDateTime(TimeOnly.MinValue);
        }
        return new(workOrderNos, machineModel, request.RobotPartId,
            null, null, null, request.PriorityId, request.RobotTypeId, expectedCompletionDate, robotOwnerContactIds);
    }

    /// <summary>
    /// Users who may own a project: active internal accounts granted project:list through an active role.
    /// The section is the user's direct section when it and its ancestors are active, otherwise null.
    /// </summary>
    internal static IQueryable<EligibleOwnerRow> EligibleOwners(YfDbContext db) =>
        from user in db.Users
        where user.UserType == UserTypes.Internal
              && user.Status == AccountStatuses.Active
              && AccessService.UsersWithPermission(db, "project:list").Contains(user.Id)
        select new EligibleOwnerRow
        {
            Id = user.Id, EmployeeNo = user.EmployeeNo, RealName = user.RealName,
            SectionId = ActiveSections(db).Where(section => section.Id == user.DepartmentId)
                .Select(section => (ulong?)section.Id).FirstOrDefault(),
            SectionName = ActiveSections(db).Where(section => section.Id == user.DepartmentId)
                .Select(section => section.Name).FirstOrDefault(),
        };

    private static IQueryable<Department> ActiveSections(YfDbContext db) =>
        db.Departments.Where(section => section.Kind == "SECTION" && section.Status == AccountStatuses.Active
            && (section.ParentId == null || db.Departments.Any(parent => parent.Id == section.ParentId
                && parent.Kind == "DEPARTMENT" && parent.Status == AccountStatuses.Active
                && (parent.ParentId == null || db.Departments.Any(root => root.Id == parent.ParentId
                    && root.Kind == "DIVISION" && root.Status == AccountStatuses.Active && root.ParentId == null)))));

    internal static async Task<ProjectMetadataInput> ValidateMetadataAsync(
        MySqlConnection conn,
        MySqlTransaction tx,
        ProjectMetadataInput input,
        ProjectRow? existing,
        ulong supplierId,
        CancellationToken ct)
    {
        await ValidateDictionaryAsync(conn, tx, input.PriorityId,
            ProjectDictionaryTypes.Priority, existing?.PriorityId, "优先级", ct);
        await ValidateDictionaryAsync(conn, tx, input.RobotTypeId,
            ProjectDictionaryTypes.RobotType, existing?.RobotTypeId, "Robot 类型", ct);
        if (input.RobotPartId is null)
        {
            if (existing?.RobotPartId is not null)
                throw ApiException.BadRequest("已选择 Robot 料号的项目不能清空料号");
            return input;
        }
        await using var db = EfDb.Use(conn, tx);
        var part = await db.RobotParts.Where(item => item.Id == input.RobotPartId.Value)
            .Select(item => new { item.Id, item.SupplierId, item.Status }).SingleOrDefaultAsync(ct);
        if (part is null) throw ApiException.BadRequest("Robot 料号不存在");
        if (part.SupplierId != supplierId) throw ApiException.BadRequest("Robot 料号不属于所选供应商");
        if (part.Id != existing?.RobotPartId)
        {
            var supplierActive = await db.Suppliers.AnyAsync(supplier => supplier.Id == supplierId
                && supplier.Status == AccountStatuses.Active, ct);
            if (!supplierActive) throw ApiException.BadRequest("供应商已被禁用");
            if (part.Status != AccountStatuses.Active) throw ApiException.BadRequest("Robot 料号已停用");
        }
        return input;
    }

    internal static async Task<ulong?> ResolveActorSectionAsync(
        YfDbContext db, ulong actorId, CancellationToken ct) =>
        await (from user in db.Users
               join section in db.Departments on user.DepartmentId equals (ulong?)section.Id
               where user.Id == actorId && section.Kind == "SECTION" && section.Status == AccountStatuses.Active
                   && (section.ParentId == null || db.Departments.Any(parent => parent.Id == section.ParentId
                       && parent.Kind == "DEPARTMENT" && parent.Status == AccountStatuses.Active
                       && (parent.ParentId == null || db.Departments.Any(root => root.Id == parent.ParentId
                           && root.Kind == "DIVISION" && root.Status == AccountStatuses.Active && root.ParentId == null))))
               select (ulong?)section.Id).SingleOrDefaultAsync(ct);

    private static async Task<MetadataDictionaryRow?> ValidateDictionaryAsync(
        MySqlConnection conn,
        MySqlTransaction tx,
        ulong? id,
        string expectedType,
        ulong? existingId,
        string label,
        CancellationToken ct)
    {
        if (id is null) return null;
        await using var db = EfDb.Use(conn, tx);
        var row = await db.ProjectDictionaries.Where(item => item.Id == id.Value)
            .Select(item => new MetadataDictionaryRow
            {
                Id = item.Id,
                Type = item.Type,
                ParentId = item.ParentId,
                Status = item.Status,
            })
            .SingleOrDefaultAsync(ct);
        if (row is null || row.Type != expectedType) throw ApiException.BadRequest(label + "不存在或类型不匹配");
        if (row.Status != AccountStatuses.Active && id != existingId) throw ApiException.BadRequest(label + "已停用");
        return row;
    }

    internal static async Task ReplaceWorkOrdersAsync(
        MySqlConnection conn, MySqlTransaction tx, ulong projectId, string[] values, CancellationToken ct)
    {
        await using var db = EfDb.Use(conn, tx);
        await db.ProjectWorkOrders.Where(order => order.ProjectId == projectId).ExecuteDeleteAsync(ct);
        if (values.Length == 0) return;
        var now = await DatabaseUtcNowAsync(db, ct);
        db.ProjectWorkOrders.AddRange(values.Select((value, index) => new ProjectWorkOrder
        {
            ProjectId = projectId,
            WorkOrderNo = value,
            SortNo = index,
            CreatedAt = now,
        }));
        await db.SaveChangesAsync(ct);
    }

    internal static async Task LoadWorkOrdersAsync(
        MySqlConnection conn, MySqlTransaction tx, IReadOnlyCollection<ProjectRow> projects, CancellationToken ct)
    {
        if (projects.Count == 0) return;
        var ids = projects.Select(project => project.Id).ToArray();
        await using var db = EfDb.Use(conn, tx);
        var byProject = (await db.ProjectWorkOrders
            .Where(order => Enumerable.Contains(ids, order.ProjectId))
            .OrderBy(order => order.ProjectId).ThenBy(order => order.SortNo).ThenBy(order => order.Id)
            .Select(order => new ProjectWorkOrderRow
            {
                ProjectId = order.ProjectId,
                WorkOrderNo = order.WorkOrderNo,
            }).ToListAsync(ct))
            .GroupBy(row => row.ProjectId).ToDictionary(group => group.Key, group => group.Select(row => row.WorkOrderNo).ToArray());
        foreach (var project in projects)
            project.WorkOrderNos = byProject.GetValueOrDefault(project.Id) ?? [];
    }

    private static async Task<ulong> CountActiveUploadsAsync(
        MySqlConnection conn, MySqlTransaction tx, ulong projectId, bool supplierOnly, CancellationToken ct)
    {
        await using var db = EfDb.Use(conn, tx);
        var dbNow = await DbClock.UtcNowAsync(db, ct);
        var rows = await db.UploadSessions
            .Where(upload => upload.ProjectId == projectId
                && (upload.Status == ProjectUploadSessionStatuses.Uploading
                    || upload.Status == ProjectUploadSessionStatuses.Merging)
                && (!supplierOnly || db.Users.Any(user => user.Id == upload.UploaderId && user.UserType == UserTypes.Supplier)))
            .Select(upload => new { upload.Status, upload.ExpiresAt })
            .ToListAsync(ct);
        return checked((ulong)rows.Count(upload => UploadActivity.IsBlocking(upload.Status, upload.ExpiresAt, dbNow)));
    }

    private static Task<DateTime> DatabaseUtcNowAsync(YfDbContext db, CancellationToken ct) =>
        DbClock.UtcNowAsync(db, ct, 3);

    internal static string ValidateNameForCreate(string? value)
    {
        var name = (value ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            throw ApiException.BadRequest("项目名称不能为空");
        }
        if (RuneCount(name) > 128)
        {
            throw ApiException.BadRequest("项目名称需为 1~128 个字符");
        }
        return name;
    }

    internal static string ValidateNameForUpdate(string? value)
    {
        var name = (value ?? string.Empty).Trim();
        if (name.Length == 0 || RuneCount(name) > 128)
        {
            throw ApiException.BadRequest("项目名称需为 1~128 个字符");
        }
        return name;
    }

    internal static void ValidateDescription(string? description)
    {
        if (description is not null && RuneCount(description) > 500)
        {
            throw ApiException.BadRequest("项目说明过长（最多 500 字）");
        }
    }

    private static DateOnly? ToDateOnly(DateTime? value) => value is null ? null : DateOnly.FromDateTime(value.Value);

    private static string? DateValue(DateTime? value) =>
        value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static int RuneCount(string value) => value.EnumerateRunes().Count();

    private sealed class ProjectCopySourceRow
    {
        public ulong ProjectId { get; init; }
        public string Name { get; init; } = string.Empty;
    }

    internal sealed record ProjectMetadataInput(
        string[] WorkOrderNos,
        string? MachineModel,
        ulong? RobotPartId,
        ulong? ResponsibleUserId,
        string? RobotOwnerName,
        ulong? SectionId,
        ulong? PriorityId,
        ulong? RobotTypeId,
        DateTime? ExpectedCompletionDate,
        ulong[] RobotOwnerContactIds);

    private sealed class MetadataDictionaryRow
    {
        public ulong Id { get; init; }
        public string Type { get; init; } = string.Empty;
        public ulong? ParentId { get; init; }
        public string Status { get; init; } = string.Empty;
    }

    internal sealed class EligibleOwnerRow
    {
        public ulong Id { get; init; }
        public string EmployeeNo { get; init; } = string.Empty;
        public string RealName { get; init; } = string.Empty;
        public ulong? SectionId { get; init; }
        public string? SectionName { get; init; }
    }

    private sealed class ProjectWorkOrderRow
    {
        public ulong ProjectId { get; init; }
        public string WorkOrderNo { get; init; } = string.Empty;
    }
}
