using System.Text;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Yf.Api.Infrastructure;
using Yf.Api.Infrastructure.Entities;
using Yf.Api.Modules.SystemManagement;

namespace Yf.Api.Modules.Projects;

internal sealed partial class ProjectService(
    AuditService audit,
    AppOptions options,
    ProjectGroupStatusService groupStatus,
    IProjectRealtimePublisher? realtime = null,
    ILogger<ProjectService>? logger = null)
{
    private static readonly Action<ILogger, ulong, Exception?> LogDeletionRealtimePublishFailure =
        LoggerMessage.Define<ulong>(LogLevel.Warning, new EventId(1, "ProjectDeletionRealtimePublishFailed"),
            "Realtime publish failed after project deletion commit for project {ProjectId}");

    internal async Task<ProjectDetailResponse> DetailAsync(MySqlConnection conn, CurrentUser actor, ulong projectId, CancellationToken ct)
    {
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        var current = await AccessService.ReadActorAsync(conn, tx, actor, ct);
        await ProjectAccessService.RequireViewForValidatedActorAsync(conn, tx, current, projectId, false, ct);
        var project = await LoadProjectAsync(conn, tx, projectId, false, ct);
        await using var db = EfDb.Use(conn, tx);
        var statusDetails = await db.Projects.Where(item => item.Id == projectId).Select(_ => new
        {
            RejectReason = db.ProjectStatusLogs
                .Where(log => log.ProjectId == projectId && log.Action == "REJECT")
                .OrderByDescending(log => log.Id).Select(log => log.Reason).FirstOrDefault(),
            LatestSubmitterId = db.ProjectStatusLogs.Where(log => log.Id == project.LatestSubmissionId)
                .Select(log => (ulong?)log.OperatorId).FirstOrDefault(),
        }).SingleAsync(ct);
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
        var unreadMessages = await MessageService.UnreadCountAsync(conn, tx, current.Id, projectId, ct);
        var result = new ProjectDetailResponse(ProjectJson.Project(project) with
        {
            UnreadMessages = unreadMessages,
            CopySource = copySource,
        })
        {
            RejectReason = statusDetails.RejectReason,
            LatestSubmitterId = statusDetails.LatestSubmitterId,
        };
        await tx.CommitAsync(ct);
        return result;
    }

    internal const string SubprojectChangedMessage = "子项目已被他人修改，请刷新后重试";

    internal async Task<ProjectResponse> UpdateSubprojectAsync(
        MySqlConnection conn,
        CurrentUser actor,
        ulong projectId,
        SubprojectUpdateRequest request,
        string? ip,
        CancellationToken ct)
    {
        if (!actor.IsInternal) throw ApiException.Forbidden();
        var name = ValidateNameForUpdate(request.Name);
        ValidateDescription(request.Description);
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        await AccessService.LockBusinessAsync(conn, tx, ct);
        var project = await LoadProjectAsync(conn, tx, projectId, true, ct);
        var current = await AccessService.RecheckActorAsync(conn, tx, actor, ct);
        await AccessService.RequirePermissionAsync(conn, tx, current, "project:update", ct);
        await ProjectAccessService.RequireViewForValidatedActorAsync(conn, tx, current, projectId, false, ct);
        // Optimistic concurrency against the row locked above: an editor that loaded an older version must
        // refresh instead of silently overwriting someone else's change.
        if (request.ExpectedUpdatedAt is { } expectedUpdatedAt
            && ProjectJson.Utc(project.UpdatedAt) != ProjectJson.Utc(expectedUpdatedAt))
            throw ApiException.Conflict(SubprojectChangedMessage);
        if (project.Status is not (ProjectStatuses.Draft or ProjectStatuses.InProgress))
            throw ApiException.Conflict("子项目当前状态不可编辑");
        await EnsureNameUniqueAsync(conn, tx, name, projectId, ct);
        await using var db = EfDb.Use(conn, tx);
        var now = await DatabaseUtcNowAsync(db, ct);
        try
        {
            var changed = await db.Projects.Where(item => item.Id == projectId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.Name, name)
                    .SetProperty(item => item.Description, request.Description)
                    .SetProperty(item => item.UpdatedAt, now), ct);
            if (changed != 1) throw ApiException.Conflict("项目已被删除，请刷新后重试");
        }
        catch (MySqlException ex) when (ex.Number == 1062)
        {
            throw ApiException.Conflict("子项目名称已存在");
        }
        catch (DbUpdateException ex) when (ex.InnerException is MySqlException { Number: 1062 })
        {
            throw ApiException.Conflict("子项目名称已存在");
        }
        await audit.WriteAsync(conn, tx, current.Id, "PROJECT_UPDATE", "project", projectId, new
        {
            name,
            projectGroupId = project.ProjectGroupId,
            changes = AuditChange.OnlyChanged(
                new("name", "子项目名称", project.Name, name),
                new("description", "子项目说明", project.Description, request.Description)),
        }, ip, ct);
        var result = ProjectJson.Project(await LoadProjectAsync(conn, tx, projectId, false, ct));
        await tx.CommitAsync(ct);
        return result;
    }

    internal async Task<ProjectSummaryResponse> SummaryAsync(MySqlConnection conn, CurrentUser actor, ulong projectId, CancellationToken ct)
    {
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        var current = await AccessService.ReadActorAsync(conn, tx, actor, ct);
        var project = await ProjectAccessService.RequireViewForValidatedActorAsync(
            conn, tx, current, projectId, false, ct);
        await using var db = EfDb.Use(conn, tx);
        var unread = await MessageService.UnreadCountAsync(conn, tx, current.Id, projectId, ct);
        var activityRevision = await ProjectActivityService.RevisionAsync(conn, tx, projectId, ct);
        var canConfirm = ProjectWorkflowRules.CanReceivePendingAcceptance(
            current,
            await ProjectAccessService.HasPermissionAsync(db, current.Id, "project:confirm", ct));
        var latestSubmissionId = project.Status == ProjectStatuses.PendingConfirmation
            ? (await LatestSubmissionAsync(conn, tx, projectId, ct)).Id
            : (ulong?)null;
        var result = new ProjectSummaryResponse(
            unread,
            activityRevision,
            canConfirm
                && project.Status == ProjectStatuses.PendingConfirmation
                && project.ConfirmSide == ProjectWorkflowRules.InternalAcceptanceSide,
            latestSubmissionId);
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
        var canListAll = await ProjectAccessService.HasPermissionAsync(
            db, current.Id, "project:create", ct)
            || await ProjectAccessService.HasPermissionAsync(db, current.Id, "project:view_all", ct);
        var query = db.Suppliers.Where(supplier => supplier.Status == AccountStatuses.Active);
        if (!canListAll)
            query = query.Where(supplier => db.ProjectGroups.Any(group => group.SupplierId == supplier.Id
                && (group.ResponsibleUserId == current.Id || group.CreatedBy == current.Id)));
        var rows = await query.OrderBy(supplier => supplier.Id)
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
        // A copy target's files share blobs with its source. Like copy and upload, take the SHA named
        // locks before the transaction, so releasing the last reference cannot race a new reference.
        string[] copiedBlobShas;
        await using (var lookup = EfDb.Use(conn))
            copiedBlobShas = await lookup.ProjectCopies.AnyAsync(copy => copy.TargetProjectId == projectId, ct)
                ? await (from file in lookup.Files
                    join blob in lookup.FileBlobs on file.BlobId equals blob.Id
                    where file.ProjectId == projectId
                    select blob.Sha256).Distinct().ToArrayAsync(ct)
                : [];
        await using var blobLeases = await Files.FileBlobStore.AcquireAsync(conn, copiedBlobShas, ct);
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        await AccessService.LockBusinessAsync(conn, tx, ct);
        var project = await LoadProjectAsync(conn, tx, projectId, true, ct);
        var current = await AccessService.RecheckActorAsync(conn, tx, actor, ct);
        await AccessService.RequirePermissionAsync(conn, tx, current, "project:delete", ct);
        await ProjectAccessService.RequireViewForValidatedActorAsync(conn, tx, current, projectId, false, ct);
        ProjectWorkflowRules.EnsureDeletable(project.Status);
        await using var db = EfDb.Use(conn, tx);
        if (await db.ProjectCopyJobs.AnyAsync(job => job.SourceProjectId == projectId
                && (job.Status == ProjectCopyJobStatuses.Pending || job.Status == ProjectCopyJobStatuses.Running), ct))
            throw ApiException.Conflict("项目仍有进行中的复制任务，请等待任务完成后再删除");
        // A source keeps its copy history; only the derived copy (the target) may be removed.
        if (await db.ProjectCopies.AnyAsync(copy => copy.SourceProjectId == projectId, ct))
            throw ApiException.Conflict("该子项目已被复制出副本，请先删除副本");
        var copyRecord = await db.ProjectCopies.AsNoTracking()
            .SingleOrDefaultAsync(copy => copy.TargetProjectId == projectId, ct);
        // Only a never-started (DRAFT) copy may discard the files the copy brought in; once started, a copy
        // follows the ordinary rule and any file or message blocks deletion. Uploads after the copy
        // (even soft-deleted) always block.
        var copyId = project.Status == ProjectStatuses.Draft ? copyRecord?.Id : null;
        ProjectWorkflowRules.EnsureNoDeletionDependencies(
            await db.Files.AnyAsync(file => file.ProjectId == projectId
                    && !db.FileCopyRefs.Any(reference => reference.CopyId == copyId && reference.TargetFileId == file.Id), ct)
                || await db.Messages.AnyAsync(message => message.ProjectId == projectId, ct),
            await db.UploadSessions.AnyAsync(upload => upload.ProjectId == projectId, ct));
        ulong[] releasedBlobIds = [];
        if (copyRecord is not null)
        {
            releasedBlobIds = await db.Files.Where(file => file.ProjectId == projectId && file.BlobId != null)
                .Select(file => file.BlobId!.Value).Distinct().OrderBy(id => id).ToArrayAsync(ct);
            var leased = copiedBlobShas.ToHashSet(StringComparer.Ordinal);
            var currentShas = await db.FileBlobs.Where(blob => releasedBlobIds.Contains(blob.Id))
                .Select(blob => blob.Sha256).ToArrayAsync(ct);
            if (currentShas.Any(sha => !leased.Contains(sha)))
                throw ApiException.Conflict("文件内容引用已变化，请刷新后重试");
            // file_copy_refs cascade with the copy row; the jobs only point at the result for navigation.
            await db.ProjectCopyJobs.Where(job => job.ResultProjectId == projectId || job.ResultCopyId == copyRecord.Id)
                .ExecuteDeleteAsync(ct);
            await db.ProjectCopies.Where(copy => copy.Id == copyRecord.Id).ExecuteDeleteAsync(ct);
            await db.Files.Where(file => file.ProjectId == projectId).ExecuteDeleteAsync(ct);
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
        var deletionAudience = await BuildDeletionAudienceAsync(db, project, ct);
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
            ? new { name = project.Name }
            : new
            {
                name = project.Name,
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
        await groupStatus.RecalculateAsync(
            conn, tx, project.ProjectGroupId, current.Id, null, ct, groupAlreadyLocked: true);
        // The main project's subproject list (and possibly its status) changed for every sibling workspace.
        var siblingIds = await db.Projects.Where(item => item.ProjectGroupId == project.ProjectGroupId)
            .OrderBy(item => item.Id).Select(item => item.Id).ToArrayAsync(ct);
        await tx.CommitAsync(ct);
        if (realtime is not null)
        {
            try { await realtime.PublishAsync(deletionAudience, RealtimeChangeKinds.Project, CancellationToken.None); }
            catch (Exception error)
            {
                if (logger is not null) LogDeletionRealtimePublishFailure(logger, projectId, error);
            }
            foreach (var siblingId in siblingIds)
            {
                try { await realtime.PublishAsync(siblingId, RealtimeChangeKinds.Project, CancellationToken.None); }
                catch (Exception error)
                {
                    if (logger is not null) LogDeletionRealtimePublishFailure(logger, siblingId, error);
                }
            }
        }
    }

    private static async Task<ProjectRealtimeAudience> BuildDeletionAudienceAsync(
        YfDbContext db, ProjectRow project, CancellationToken ct)
    {
        var viewAllUsers = await db.Users.Where(user => user.Status == AccountStatuses.Active
                && AccessService.UsersWithPermission(db, "project:view_all").Contains(user.Id))
            .Select(user => user.Id).ToArrayAsync(ct);
        var supplierUsers = await db.Users.Where(user => user.SupplierId == project.SupplierId
                && user.Status == AccountStatuses.Active)
            .Select(user => user.Id).ToArrayAsync(ct);
        var creatorId = await db.ProjectGroups.Where(group => group.Id == project.ProjectGroupId)
            .Select(group => (ulong?)group.CreatedBy).SingleOrDefaultAsync(ct);
        return new(project.Id, project.SupplierId, project.ResponsibleUserId,
            viewAllUsers.ToHashSet(), supplierUsers.ToHashSet(), creatorId);
    }

    private static async Task<ProjectRow> LoadProjectAsync(
        MySqlConnection conn,
        MySqlTransaction? tx,
        ulong projectId,
        bool forUpdate,
        CancellationToken ct)
    {
        await using var db = EfDb.Use(conn, tx);
        if (forUpdate)
        {
            var groupId = await db.Projects.Where(item => item.Id == projectId)
                .Select(item => (ulong?)item.ProjectGroupId).SingleOrDefaultAsync(ct);
            if (groupId is null) throw ApiException.NotFound();
            if (await db.ProjectGroups
                    .FromSqlInterpolated($"SELECT * FROM project_groups WHERE id={groupId.Value} FOR UPDATE")
                    .AsNoTracking().SingleOrDefaultAsync(ct) is null)
                throw ApiException.NotFound();
            if (await db.Projects
                    .FromSqlInterpolated($"SELECT * FROM projects WHERE id={projectId} FOR UPDATE")
                    .AsNoTracking().SingleOrDefaultAsync(ct) is null)
                throw ApiException.NotFound();
        }

        var row = await ProjectQueries.Rows(db).SingleOrDefaultAsync(item => item.Id == projectId, ct);
        if (row is null) throw ApiException.NotFound();
        row.WorkOrderNos = await db.ProjectWorkOrders.Where(order => order.ProjectId == projectId)
            .OrderBy(order => order.SortNo).ThenBy(order => order.Id)
            .Select(order => order.WorkOrderNo).ToArrayAsync(ct);
        return row;
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
            throw ApiException.Conflict("子项目名称已存在");
        }
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
            null, null, request.PriorityId, request.RobotTypeId, expectedCompletionDate);
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
        MySqlConnection conn, MySqlTransaction tx, ulong projectId, CancellationToken ct)
    {
        await using var db = EfDb.Use(conn, tx);
        var count = await db.UploadSessions.LongCountAsync(upload =>
            upload.ProjectId == projectId
            && (upload.Status == ProjectUploadSessionStatuses.Uploading
                || upload.Status == ProjectUploadSessionStatuses.Merging), ct);
        return checked((ulong)count);
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
        ulong? SectionId,
        ulong? PriorityId,
        ulong? RobotTypeId,
        DateTime? ExpectedCompletionDate);

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
