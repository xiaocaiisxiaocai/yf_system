using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Yf.Api.Infrastructure;
using Yf.Api.Infrastructure.Entities;

namespace Yf.Api.Modules.Projects;

internal sealed class ProjectGroupService(
    AuditService audit,
    ProjectGroupStatusService groupStatus,
    IProjectRealtimePublisher? realtime = null,
    ILogger<ProjectGroupService>? logger = null)
{
    private static readonly Action<ILogger, ulong, Exception?> LogRealtimePublishFailure =
        LoggerMessage.Define<ulong>(LogLevel.Warning, new EventId(1, "ProjectGroupRealtimePublishFailed"),
            "Realtime publish failed after main project commit for project {ProjectId}");

    internal async Task<PageResponse<ProjectGroupResponse>> ListAsync(
        MySqlConnection conn,
        CurrentUser actor,
        ulong page,
        ulong pageSize,
        string? keyword,
        string? status,
        ulong? supplierId,
        CancellationToken ct)
    {
        var (actualPage, size) = ProjectJson.ClampPage(page, pageSize);
        status = NormalizeGroupStatus(status);
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        var current = await AccessService.ReadActorAsync(conn, tx, actor, ct);
        await using var db = EfDb.Use(conn, tx);
        var query = await ProjectGroupAccessService.VisibleQueryAsync(db, current, ct);
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var value = keyword.Trim();
            query = query.Where(group => EF.Functions.Like(
                group.Name, QueryValues.ContainsPattern(value), QueryValues.LikeEscape));
        }
        if (status is not null) query = query.Where(group => group.Status == status);
        if (supplierId is not null) query = query.Where(group => group.SupplierId == supplierId.Value);
        var total = (ulong)await query.LongCountAsync(ct);
        var offset = (actualPage - 1) * size;
        var rows = await GroupRows(db, query).OrderByDescending(group => group.Id)
            .Page(offset, size).ToArrayAsync(ct);
        await LoadWorkOrdersAsync(db, rows, ct);
        await LoadGroupUnreadAsync(db, rows, current.Id, ct);
        await tx.CommitAsync(ct);
        return ProjectJson.Page(rows.Select(ProjectJson.ProjectGroup).ToArray(), total, actualPage, size);
    }

    internal async Task<ProjectGroupDetailResponse> DetailAsync(
        MySqlConnection conn,
        CurrentUser actor,
        ulong groupId,
        CancellationToken ct)
    {
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        var current = await AccessService.ReadActorAsync(conn, tx, actor, ct);
        await ProjectGroupAccessService.RequireViewAsync(conn, tx, current, groupId, false, ct);
        await using var db = EfDb.Use(conn, tx);
        var group = await LoadGroupAsync(db, groupId, current.Id, ct);
        var projects = await ProjectQueries.Rows(db).Where(project => project.ProjectGroupId == groupId)
            .OrderBy(project => project.Id).ToArrayAsync(ct);
        await LoadProjectExtrasAsync(db, projects, current.Id, ct);
        await tx.CommitAsync(ct);
        return new ProjectGroupDetailResponse(ProjectJson.ProjectGroup(group), projects.Select(ProjectJson.Project).ToArray());
    }

    internal async Task<ProjectGroupResponse> CreateAsync(
        MySqlConnection conn,
        CurrentUser actor,
        ProjectUpsertRequest request,
        string? ip,
        CancellationToken ct)
    {
        if (!actor.IsInternal) throw ApiException.Forbidden();
        var name = ProjectService.ValidateNameForCreate(request.Name);
        ProjectService.ValidateDescription(request.Description);
        var childNames = NormalizeSubprojectNames(request.SubprojectNames);
        var metadata = ProjectService.NormalizeMetadata(request, true);
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        await AccessService.LockBusinessAsync(conn, tx, ct);
        var current = await AccessService.RecheckActorAsync(conn, tx, actor, ct);
        await AccessService.RequirePermissionAsync(conn, tx, current, "project:list", ct);
        await AccessService.RequirePermissionAsync(conn, tx, current, "project:create", ct);
        await using var db = EfDb.Use(conn, tx);
        await EnsureSupplierAsync(db, request.SupplierId, ct);
        await EnsureGroupNameUniqueAsync(db, name, null, ct);
        if (await db.Projects.AnyAsync(project => Enumerable.Contains(childNames, project.Name), ct))
            throw ApiException.Conflict("子项目名称已存在");
        metadata = metadata with
        {
            ResponsibleUserId = current.Id,
            SectionId = await ProjectService.ResolveActorSectionAsync(db, current.Id, ct),
        };
        metadata = await ProjectService.ValidateMetadataAsync(conn, tx, metadata, null, request.SupplierId, ct);

        var group = new ProjectGroup
        {
            Name = name,
            Description = request.Description,
            SupplierId = request.SupplierId,
            Status = ProjectStatuses.Draft,
            CreatedBy = current.Id,
            MachineModel = metadata.MachineModel,
            RobotPartId = metadata.RobotPartId,
            LegacyRobotModelName = null,
            ResponsibleUserId = metadata.ResponsibleUserId,
            RobotOwnerName = metadata.RobotOwnerName,
            SectionId = metadata.SectionId,
            PriorityId = metadata.PriorityId,
            RobotTypeId = metadata.RobotTypeId,
            ExpectedCompletionDate = ToDateOnly(metadata.ExpectedCompletionDate),
        };
        db.ProjectGroups.Add(group);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException error) when (error.InnerException is MySqlException { Number: 1062 })
        { throw ApiException.Conflict("主项目名称已存在"); }

        await ReplaceGroupWorkOrdersAsync(db, group.Id, metadata.WorkOrderNos, ct);
        var children = childNames.Select(childName => NewChild(
            group.Id, childName, null, request.SupplierId, current.Id, metadata)).ToArray();
        db.Projects.AddRange(children);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException error) when (error.InnerException is MySqlException { Number: 1062 })
        { throw ApiException.Conflict("子项目名称已存在"); }
        var createdAt = await DbClock.UtcNowAsync(db, ct, 3);
        AddChildRecords(db, children, metadata.WorkOrderNos, current.Id, "CREATE", createdAt);
        db.ProjectGroupStatusLogs.Add(new ProjectGroupStatusLog
        {
            ProjectGroupId = group.Id,
            FromStatus = null,
            ToStatus = ProjectStatuses.Draft,
            Action = "CREATE",
            TriggerProjectId = children.FirstOrDefault()?.Id,
            OperatorId = current.Id,
            CreatedAt = createdAt,
        });
        await db.SaveChangesAsync(ct);

        var createAudits = children.Select((child, index) => new AuditWrite(
            "PROJECT_CREATE", "project", child.Id, new
            {
                name = childNames[index],
                projectGroupId = group.Id,
                projectGroupName = name,
                inheritedFromMainProject = true,
            })).Append(new AuditWrite("PROJECT_GROUP_CREATE", "project_group", group.Id, new
        {
            name,
            request.SupplierId,
            metadata.WorkOrderNos,
            metadata.MachineModel,
            metadata.RobotPartId,
            metadata.ResponsibleUserId,
            metadata.RobotOwnerName,
            metadata.SectionId,
            metadata.PriorityId,
            metadata.RobotTypeId,
            metadata.ExpectedCompletionDate,
            subprojectIds = children.Select(child => child.Id).ToArray(),
            subprojectNames = childNames,
        })).ToArray();
        await audit.WriteBatchAsync(conn, tx, current.Id, createAudits, ip, ct);
        var result = ProjectJson.ProjectGroup(await LoadGroupAsync(db, group.Id, current.Id, ct, loadUnread: false));
        await tx.CommitAsync(ct);
        return result;
    }

    internal async Task<ProjectGroupResponse> UpdateAsync(
        MySqlConnection conn,
        CurrentUser actor,
        ulong groupId,
        ProjectUpsertRequest request,
        string? ip,
        CancellationToken ct)
    {
        if (!actor.IsInternal) throw ApiException.Forbidden();
        var name = ProjectService.ValidateNameForUpdate(request.Name);
        ProjectService.ValidateDescription(request.Description);
        var metadata = ProjectService.NormalizeMetadata(request, false);
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        await AccessService.LockBusinessAsync(conn, tx, ct);
        var current = await AccessService.RecheckActorAsync(conn, tx, actor, ct);
        var access = await ProjectGroupAccessService.RequireViewAsync(conn, tx, current, groupId, true, ct);
        await AccessService.RequirePermissionAsync(conn, tx, current, "project:update", ct);
        if (access.Status is ProjectStatuses.Completed or ProjectStatuses.Terminated)
            throw ApiException.Conflict("主项目已结束，不能修改公共资料");
        if (access.SupplierId != request.SupplierId)
            throw ApiException.BadRequest("主项目创建后不可更换供应商");
        await using var db = EfDb.Use(conn, tx);
        if (await db.Projects.AnyAsync(project => project.ProjectGroupId == groupId
                && project.Status == ProjectStatuses.PendingConfirmation, ct))
            throw ApiException.Conflict("存在待验收子项目，暂不能修改主项目资料");
        var before = await LoadGroupAsync(db, groupId, current.Id, ct, loadUnread: false);
        await EnsureGroupNameUniqueAsync(db, name, groupId, ct);
        metadata = await ProjectService.ValidateMetadataAsync(conn, tx, metadata, new ProjectRow
        {
            RobotPartId = before.RobotPartId,
            PriorityId = before.PriorityId,
            RobotTypeId = before.RobotTypeId,
        }, access.SupplierId, ct);
        metadata = metadata with
        {
            ResponsibleUserId = before.ResponsibleUserId,
            SectionId = before.SectionId,
        };

        var workOrdersChanged = !before.WorkOrderNos.SequenceEqual(metadata.WorkOrderNos, StringComparer.Ordinal);
        var inheritedMetadataChanged = before.MachineModel != metadata.MachineModel
            || before.RobotPartId != metadata.RobotPartId
            || before.PriorityId != metadata.PriorityId
            || before.RobotTypeId != metadata.RobotTypeId
            || before.RobotOwnerName != metadata.RobotOwnerName
            || before.ExpectedCompletionDate != metadata.ExpectedCompletionDate;
        var groupChanged = before.Name != name
            || before.Description != request.Description
            || workOrdersChanged
            || inheritedMetadataChanged;
        if (!groupChanged)
        {
            var unchanged = ProjectJson.ProjectGroup(before);
            await tx.CommitAsync(ct);
            return unchanged;
        }

        try
        {
            await db.ProjectGroups.Where(group => group.Id == groupId).ExecuteUpdateAsync(setters => setters
                .SetProperty(group => group.Name, name)
                .SetProperty(group => group.Description, request.Description)
                .SetProperty(group => group.MachineModel, metadata.MachineModel)
                .SetProperty(group => group.RobotPartId, metadata.RobotPartId)
                .SetProperty(group => group.ResponsibleUserId, metadata.ResponsibleUserId)
                .SetProperty(group => group.SectionId, metadata.SectionId)
                .SetProperty(group => group.PriorityId, metadata.PriorityId)
                .SetProperty(group => group.RobotTypeId, metadata.RobotTypeId)
                .SetProperty(group => group.RobotOwnerName, metadata.RobotOwnerName)
                .SetProperty(group => group.ExpectedCompletionDate, ToDateOnly(metadata.ExpectedCompletionDate)), ct);
        }
        catch (DbUpdateException error) when (error.InnerException is MySqlException { Number: 1062 })
        { throw ApiException.Conflict("主项目名称已存在"); }

        var childIds = inheritedMetadataChanged || workOrdersChanged
            ? await db.Projects.Where(project => project.ProjectGroupId == groupId
                    && project.Status != ProjectStatuses.Completed)
                .OrderBy(project => project.Id).Select(project => project.Id).ToArrayAsync(ct)
            : [];
        if (inheritedMetadataChanged)
        {
            await db.Projects.Where(project => project.ProjectGroupId == groupId
                    && project.Status != ProjectStatuses.Completed)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(project => project.MachineModel, metadata.MachineModel)
                    .SetProperty(project => project.RobotPartId, metadata.RobotPartId)
                    .SetProperty(project => project.PriorityId, metadata.PriorityId)
                    .SetProperty(project => project.RobotTypeId, metadata.RobotTypeId)
                    .SetProperty(project => project.RobotOwnerName, metadata.RobotOwnerName)
                    .SetProperty(project => project.ExpectedCompletionDate, ToDateOnly(metadata.ExpectedCompletionDate)), ct);
            if (metadata.RobotPartId is not null)
            {
                await db.ProjectGroups.Where(group => group.Id == groupId)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(group => group.LegacyRobotModelName, (string?)null), ct);
                await db.Projects.Where(project => project.ProjectGroupId == groupId
                        && project.Status != ProjectStatuses.Completed)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(project => project.LegacyRobotModelName, (string?)null), ct);
            }
        }
        if (workOrdersChanged)
        {
            await ReplaceGroupWorkOrdersAsync(db, groupId, metadata.WorkOrderNos, ct);
            await db.ProjectWorkOrders.Where(order => db.Projects.Any(project => project.Id == order.ProjectId
                    && project.ProjectGroupId == groupId && project.Status != ProjectStatuses.Completed))
                .ExecuteDeleteAsync(ct);
            db.ProjectWorkOrders.AddRange(childIds.SelectMany(projectId => metadata.WorkOrderNos.Select((value, index) =>
                new ProjectWorkOrder { ProjectId = projectId, WorkOrderNo = value, SortNo = index })));
            await db.SaveChangesAsync(ct);
        }

        var updateAudits = childIds.Select(childId => new AuditWrite(
            "PROJECT_UPDATE", "project", childId, new
            {
                projectGroupId = groupId,
                inheritedFromMainProject = true,
                changedByMainProject = true,
            })).Append(new AuditWrite("PROJECT_GROUP_UPDATE", "project_group", groupId, new
        {
            name,
            changes = AuditChange.OnlyChanged(
                new("name", "主项目名称", before.Name, name),
                new("description", "项目说明", before.Description, request.Description),
                new("workOrderNos", "工令号", before.WorkOrderNos, metadata.WorkOrderNos),
                new("machineModel", "机型", before.MachineModel, metadata.MachineModel),
                new("robotPartId", "Robot 料号", before.RobotPartId, metadata.RobotPartId),
                new("priorityId", "优先级", before.PriorityId, metadata.PriorityId),
                new("robotTypeId", "Robot 类型", before.RobotTypeId, metadata.RobotTypeId),
                new("robotOwnerName", "Robot 负责人", before.RobotOwnerName, metadata.RobotOwnerName),
                new("expectedCompletionDate", "预计完成日期",
                    DateValue(before.ExpectedCompletionDate), DateValue(metadata.ExpectedCompletionDate))),
        })).ToArray();
        await audit.WriteBatchAsync(conn, tx, current.Id, updateAudits, ip, ct);
        var result = ProjectJson.ProjectGroup(await LoadGroupAsync(db, groupId, current.Id, ct, loadUnread: false));
        // Name and description changes write no subproject activity, so tell every subproject (completed ones
        // too: they still show the main project's name) directly.
        var affected = await db.Projects.Where(project => project.ProjectGroupId == groupId)
            .OrderBy(project => project.Id).Select(project => project.Id).ToArrayAsync(ct);
        await tx.CommitAsync(ct);
        await PublishAfterCommitAsync(affected);
        return result;
    }

    internal async Task<ProjectGroupResponse> TransferAsync(
        MySqlConnection conn,
        CurrentUser actor,
        ulong groupId,
        ProjectGroupTransferRequest request,
        string? ip,
        CancellationToken ct)
    {
        if (!actor.IsInternal) throw ApiException.Forbidden();
        if (request.ResponsibleUserId is not ulong targetId || targetId == 0)
            throw ApiException.BadRequest("请选择新的负责人");
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        await AccessService.LockBusinessAsync(conn, tx, ct);
        var current = await AccessService.RecheckActorAsync(conn, tx, actor, ct);
        var access = await ProjectGroupAccessService.RequireViewAsync(conn, tx, current, groupId, true, ct);
        await AccessService.RequirePermissionAsync(conn, tx, current, "project:transfer", ct);
        if (access.ResponsibleUserId == targetId) throw ApiException.BadRequest("新负责人与当前负责人相同");
        await using var db = EfDb.Use(conn, tx);
        // Reviewers and pending-acceptance mail are derived from the owner, so keep them stable during acceptance.
        if (await db.Projects.AnyAsync(project => project.ProjectGroupId == groupId
                && project.Status == ProjectStatuses.PendingConfirmation, ct))
            throw ApiException.Conflict("存在待验收子项目，暂不能变更负责人");
        var owner = await ProjectService.EligibleOwners(db).Where(row => row.Id == targetId).SingleOrDefaultAsync(ct)
            ?? throw ApiException.BadRequest("新负责人必须是启用且拥有项目列表权限的公司内部账号");
        var before = await LoadGroupAsync(db, groupId, current.Id, ct, loadUnread: false);
        // Snapshot who can see each subproject before access moves, so the former owner is told as well.
        var audiencesBefore = await ProjectRealtimeAuthorizer.SnapshotAudiencesAsync(db,
            await db.Projects.Where(project => project.ProjectGroupId == groupId)
                .Select(project => project.Id).ToArrayAsync(ct), ct);

        await db.ProjectGroups.Where(group => group.Id == groupId).ExecuteUpdateAsync(setters => setters
            .SetProperty(group => group.ResponsibleUserId, owner.Id)
            .SetProperty(group => group.SectionId, owner.SectionId), ct);
        // Access follows the owner, so completed subprojects move with the main project as well.
        var childIds = await db.Projects.Where(project => project.ProjectGroupId == groupId)
            .OrderBy(project => project.Id).Select(project => project.Id).ToArrayAsync(ct);
        await db.Projects.Where(project => project.ProjectGroupId == groupId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(project => project.ResponsibleUserId, owner.Id)
                .SetProperty(project => project.SectionId, owner.SectionId), ct);

        var responsibleChange = new AuditChange("responsibleUserId", "负责人",
            before.ResponsibleUserId is null ? null : new
            {
                id = before.ResponsibleUserId, employeeNo = before.ResponsibleUserEmployeeNo, name = before.ResponsibleUserName,
            },
            new { id = owner.Id, employeeNo = owner.EmployeeNo, name = owner.RealName });
        var sectionChange = new AuditChange("sectionId", "课别",
            before.SectionId is null ? null : new { id = before.SectionId, name = before.SectionName },
            owner.SectionId is null ? null : new { id = owner.SectionId, name = owner.SectionName });
        var audits = childIds.Select(childId => new AuditWrite(
            "PROJECT_UPDATE", "project", childId, new
            {
                projectGroupId = groupId,
                inheritedFromMainProject = true,
                changedByMainProject = true,
                changes = AuditChange.OnlyChanged(responsibleChange, sectionChange),
            })).Append(new AuditWrite("PROJECT_GROUP_TRANSFER", "project_group", groupId, new
        {
            name = before.Name,
            changes = AuditChange.OnlyChanged(responsibleChange, sectionChange),
        })).ToArray();
        await audit.WriteBatchAsync(conn, tx, current.Id, audits, ip, ct);
        var result = ProjectJson.ProjectGroup(await LoadGroupAsync(db, groupId, current.Id, ct, loadUnread: false));
        await tx.CommitAsync(ct);
        await PublishAfterCommitAsync(audiencesBefore.Select(audience => audience with
        {
            ResponsibleUserId = owner.Id,
            FormerInternalUserIds = audience.ResponsibleUserId is ulong formerOwner && formerOwner != owner.Id
                ? new HashSet<ulong> { formerOwner }
                : null,
        }));
        return result;
    }

    /// <summary>Pushes a coalesced <c>project</c> signal after commit; a push failure never undoes the commit.</summary>
    private async Task PublishAfterCommitAsync(IEnumerable<ulong> projectIds)
    {
        if (realtime is null) return;
        foreach (var projectId in projectIds)
        {
            try { await realtime.PublishAsync(projectId, RealtimeChangeKinds.Project, CancellationToken.None); }
            catch (Exception error)
            {
                if (logger is not null) LogRealtimePublishFailure(logger, projectId, error);
            }
        }
    }

    private async Task PublishAfterCommitAsync(IEnumerable<ProjectRealtimeAudience> audiences)
    {
        if (realtime is null) return;
        foreach (var audience in audiences)
        {
            try { await realtime.PublishAsync(audience, RealtimeChangeKinds.Project, CancellationToken.None); }
            catch (Exception error)
            {
                if (logger is not null) LogRealtimePublishFailure(logger, audience.ProjectId, error);
            }
        }
    }

    internal async Task<ProjectResponse> CreateSubprojectAsync(
        MySqlConnection conn,
        CurrentUser actor,
        ulong groupId,
        SubprojectUpsertRequest request,
        string? ip,
        CancellationToken ct)
    {
        if (!actor.IsInternal) throw ApiException.Forbidden();
        var name = ProjectService.ValidateNameForCreate(request.Name);
        ProjectService.ValidateDescription(request.Description);
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        await AccessService.LockBusinessAsync(conn, tx, ct);
        var current = await AccessService.RecheckActorAsync(conn, tx, actor, ct);
        var access = await ProjectGroupAccessService.RequireViewAsync(conn, tx, current, groupId, true, ct);
        await AccessService.RequirePermissionAsync(conn, tx, current, "project:create", ct);
        if (access.Status is ProjectStatuses.Completed or ProjectStatuses.Terminated)
            throw ApiException.Conflict("主项目已结束，不能新增子项目");
        await using var db = EfDb.Use(conn, tx);
        await EnsureProjectNameUniqueAsync(db, name, null, ct);
        var group = await LoadGroupAsync(db, groupId, current.Id, ct, loadUnread: false);
        var metadata = Metadata(group);
        var child = NewChild(groupId, name, request.Description, group.SupplierId, current.Id, metadata);
        db.Projects.Add(child);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException error) when (error.InnerException is MySqlException { Number: 1062 })
        { throw ApiException.Conflict("子项目名称已存在"); }
        AddChildRecords(db, [child], metadata.WorkOrderNos, current.Id, "CREATE", await DbClock.UtcNowAsync(db, ct, 3));
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(conn, tx, current.Id, "PROJECT_CREATE", "project", child.Id, new
        {
            name,
            projectGroupId = groupId,
            projectGroupName = group.Name,
            inheritedFromMainProject = true,
        }, ip, ct);
        await groupStatus.RecalculateAsync(conn, tx, groupId, current.Id, child.Id, ct, groupAlreadyLocked: true);
        var result = ProjectJson.Project(await LoadChildAsync(db, child.Id, current.Id, ct, loadUnread: false));
        await tx.CommitAsync(ct);
        return result;
    }

    internal async Task DeleteAsync(
        MySqlConnection conn,
        CurrentUser actor,
        ulong groupId,
        string? ip,
        CancellationToken ct)
    {
        if (!actor.IsInternal) throw ApiException.Forbidden();
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        await AccessService.LockBusinessAsync(conn, tx, ct);
        var current = await AccessService.RecheckActorAsync(conn, tx, actor, ct);
        await ProjectGroupAccessService.RequireViewAsync(conn, tx, current, groupId, true, ct);
        await AccessService.RequirePermissionAsync(conn, tx, current, "project:delete", ct);
        await using var db = EfDb.Use(conn, tx);
        var group = await LoadGroupAsync(db, groupId, current.Id, ct, loadUnread: false);
        if (group.SubprojectCount > 0) throw ApiException.Conflict("主项目仍有子项目，请先逐个处理子项目");
        await audit.WriteAsync(conn, tx, current.Id, "PROJECT_GROUP_DELETE", "project_group", groupId,
            new { name = group.Name }, ip, ct);
        var deleted = await db.ProjectGroups.Where(item => item.Id == groupId).ExecuteDeleteAsync(ct);
        if (deleted != 1) throw ApiException.Conflict("主项目已被删除，请刷新后重试");
        await tx.CommitAsync(ct);
    }

    private static Project NewChild(
        ulong groupId,
        string name,
        string? description,
        ulong supplierId,
        ulong actorId,
        ProjectService.ProjectMetadataInput metadata) => new()
    {
        ProjectGroupId = groupId,
        Name = name,
        Description = description,
        SupplierId = supplierId,
        Status = ProjectStatuses.Draft,
        ConfirmSide = null,
        CreatedBy = actorId,
        MachineModel = metadata.MachineModel,
        RobotPartId = metadata.RobotPartId,
        LegacyRobotModelName = null,
        ResponsibleUserId = metadata.ResponsibleUserId,
        RobotOwnerName = metadata.RobotOwnerName,
        SectionId = metadata.SectionId,
        PriorityId = metadata.PriorityId,
        RobotTypeId = metadata.RobotTypeId,
        ExpectedCompletionDate = ToDateOnly(metadata.ExpectedCompletionDate),
    };

    private static void AddChildRecords(
        YfDbContext db,
        IReadOnlyCollection<Project> children,
        IReadOnlyList<string> workOrderNos,
        ulong actorId,
        string action,
        DateTime now)
    {
        db.ProjectWorkOrders.AddRange(children.SelectMany(child => workOrderNos.Select((value, index) =>
            new ProjectWorkOrder { ProjectId = child.Id, WorkOrderNo = value, SortNo = index, CreatedAt = now })));
        db.ProjectStatusLogs.AddRange(children.Select(child => new ProjectStatusLog
        {
            ProjectId = child.Id,
            FromStatus = null,
            ToStatus = ProjectStatuses.Draft,
            Action = action,
            OperatorId = actorId,
            ConfirmSide = null,
            Reason = null,
            CreatedAt = now,
        }));
    }

    // Related labels and status counts are joined once. Unread counts are loaded per page by
    // LoadGroupUnreadAsync because they depend on the current user and the bounded unread window.
    private static IQueryable<ProjectGroupRow> GroupRows(
        YfDbContext db,
        IQueryable<ProjectGroup> query)
    {
        var counts = from project in db.Projects
                     group project by project.ProjectGroupId into perGroup
                     select new
                     {
                         ProjectGroupId = perGroup.Key,
                         Total = (long?)perGroup.LongCount(),
                         Completed = (long?)perGroup.LongCount(project => project.Status == ProjectStatuses.Completed),
                         Pending = (long?)perGroup.LongCount(project => project.Status == ProjectStatuses.PendingConfirmation),
                         Terminated = (long?)perGroup.LongCount(project => project.Status == ProjectStatuses.Terminated),
                     };
        return
            from mainProject in query
            join supplierValue in db.Suppliers on mainProject.SupplierId equals supplierValue.Id into suppliers
            from supplier in suppliers.DefaultIfEmpty()
            join creatorValue in db.Users on mainProject.CreatedBy equals creatorValue.Id into creators
            from creator in creators.DefaultIfEmpty()
            join partValue in db.RobotParts on mainProject.RobotPartId equals (ulong?)partValue.Id into parts
            from part in parts.DefaultIfEmpty()
            join ownerValue in db.Users on mainProject.ResponsibleUserId equals (ulong?)ownerValue.Id into owners
            from owner in owners.DefaultIfEmpty()
            join sectionValue in db.Departments.Where(item => item.Kind == "SECTION")
                on mainProject.SectionId equals (ulong?)sectionValue.Id into sections
            from section in sections.DefaultIfEmpty()
            join priorityValue in db.ProjectDictionaries on mainProject.PriorityId equals (ulong?)priorityValue.Id into priorities
            from priority in priorities.DefaultIfEmpty()
            join robotTypeValue in db.ProjectDictionaries on mainProject.RobotTypeId equals (ulong?)robotTypeValue.Id into robotTypes
            from robotType in robotTypes.DefaultIfEmpty()
            join countValue in counts on mainProject.Id equals countValue.ProjectGroupId into countRows
            from count in countRows.DefaultIfEmpty()
            select new ProjectGroupRow
            {
                Id = mainProject.Id,
                Name = mainProject.Name,
                Description = mainProject.Description,
                SupplierId = mainProject.SupplierId,
                SupplierName = supplier.Name,
                Status = mainProject.Status,
                CreatedBy = mainProject.CreatedBy,
                CreatedByName = creator.RealName,
                MachineModel = mainProject.MachineModel,
                RobotPartId = mainProject.RobotPartId,
                RobotPartNumber = part.PartNumber,
                RobotModelName = part.Model ?? mainProject.LegacyRobotModelName,
                LegacyRobotModelName = mainProject.LegacyRobotModelName,
                ResponsibleUserId = mainProject.ResponsibleUserId,
                ResponsibleUserEmployeeNo = owner.EmployeeNo,
                ResponsibleUserName = owner.RealName,
                RobotOwnerName = mainProject.RobotOwnerName,
                SectionId = mainProject.SectionId,
                SectionName = section.Name,
                PriorityId = mainProject.PriorityId,
                PriorityName = priority.Name,
                RobotTypeId = mainProject.RobotTypeId,
                RobotTypeName = robotType.Name,
                ExpectedCompletionDate = mainProject.ExpectedCompletionDate.HasValue
                    ? mainProject.ExpectedCompletionDate.GetValueOrDefault().ToDateTime(TimeOnly.MinValue) : null,
                CompletedAt = mainProject.CompletedAt,
                CreatedAt = mainProject.CreatedAt,
                UpdatedAt = mainProject.UpdatedAt,
                SubprojectCount = (ulong)(count.Total ?? 0),
                CompletedCount = (ulong)(count.Completed ?? 0),
                PendingCount = (ulong)(count.Pending ?? 0),
                TerminatedCount = (ulong)(count.Terminated ?? 0),
            };
    }

    /// <summary>Unread messages (within the unread window) per group, for all groups in one query.</summary>
    private static async Task LoadGroupUnreadAsync(
        YfDbContext db, IReadOnlyCollection<ProjectGroupRow> groups, ulong userId, CancellationToken ct)
    {
        if (groups.Count == 0) return;
        var groupIds = groups.Select(group => group.Id).ToArray();
        var cutoff = await UnreadWindow.CutoffAsync(db, ct);
        var counts = await (
            from message in db.Messages
            join project in db.Projects on message.ProjectId equals project.Id
            where Enumerable.Contains(groupIds, project.ProjectGroupId)
                && message.Status == "NORMAL" && message.SenderId != userId && message.CreatedAt >= cutoff
                && !db.MessageReads.Any(receipt => receipt.MessageId == message.Id && receipt.UserId == userId)
            group message by project.ProjectGroupId into perGroup
            select new { GroupId = perGroup.Key, Count = perGroup.LongCount() })
            .ToDictionaryAsync(row => row.GroupId, row => (ulong)row.Count, ct);
        foreach (var group in groups) group.UnreadMessages = counts.GetValueOrDefault(group.Id);
    }

    private static async Task<ProjectGroupRow> LoadGroupAsync(
        YfDbContext db, ulong groupId, ulong userId, CancellationToken ct, bool loadUnread = true)
    {
        var row = await GroupRows(db, db.ProjectGroups.Where(group => group.Id == groupId))
            .SingleOrDefaultAsync(ct) ?? throw ApiException.NotFound();
        await LoadWorkOrdersAsync(db, [row], ct);
        if (loadUnread) await LoadGroupUnreadAsync(db, [row], userId, ct);
        return row;
    }

    private static async Task<ProjectRow> LoadChildAsync(
        YfDbContext db, ulong projectId, ulong userId, CancellationToken ct, bool loadUnread = true)
    {
        var row = await ProjectQueries.Rows(db).SingleOrDefaultAsync(project => project.Id == projectId, ct)
            ?? throw ApiException.NotFound();
        await LoadProjectExtrasAsync(db, [row], userId, ct, loadUnread);
        return row;
    }

    private static async Task LoadWorkOrdersAsync(
        YfDbContext db, IReadOnlyCollection<ProjectGroupRow> groups, CancellationToken ct)
    {
        if (groups.Count == 0) return;
        var ids = groups.Select(group => group.Id).ToArray();
        var rows = await db.ProjectGroupWorkOrders
            .Where(order => Enumerable.Contains(ids, order.ProjectGroupId))
            .OrderBy(order => order.ProjectGroupId).ThenBy(order => order.SortNo).ThenBy(order => order.Id)
            .Select(order => new GroupWorkOrderRow { ProjectGroupId = order.ProjectGroupId, WorkOrderNo = order.WorkOrderNo })
            .ToArrayAsync(ct);
        var lookup = rows.GroupBy(row => row.ProjectGroupId)
            .ToDictionary(group => group.Key, group => group.Select(row => row.WorkOrderNo).ToArray());
        foreach (var group in groups) group.WorkOrderNos = lookup.GetValueOrDefault(group.Id) ?? [];
    }

    private static async Task LoadProjectExtrasAsync(
        YfDbContext db, IReadOnlyCollection<ProjectRow> projects, ulong userId, CancellationToken ct,
        bool loadUnread = true)
    {
        if (projects.Count == 0) return;
        var ids = projects.Select(project => project.Id).ToArray();
        var workOrders = await db.ProjectWorkOrders.Where(order => Enumerable.Contains(ids, order.ProjectId))
            .OrderBy(order => order.ProjectId).ThenBy(order => order.SortNo).ThenBy(order => order.Id)
            .Select(order => new ProjectWorkOrderValue(order.ProjectId, order.WorkOrderNo)).ToArrayAsync(ct);
        var workOrderLookup = workOrders.GroupBy(row => row.ProjectId)
            .ToDictionary(group => group.Key, group => group.Select(row => row.WorkOrderNo).ToArray());
        var unreadLookup = new Dictionary<ulong, ulong>();
        if (loadUnread)
        {
            var cutoff = await UnreadWindow.CutoffAsync(db, ct);
            var unreadRows = await db.Messages.Where(message => Enumerable.Contains(ids, message.ProjectId)
                    && message.Status == "NORMAL" && message.SenderId != userId && message.CreatedAt >= cutoff
                    && !db.MessageReads.Any(receipt => receipt.MessageId == message.Id && receipt.UserId == userId))
                .GroupBy(message => message.ProjectId)
                .Select(group => new UnreadValue(group.Key, group.LongCount())).ToArrayAsync(ct);
            unreadLookup = unreadRows.ToDictionary(row => row.ProjectId, row => (ulong)row.Count);
        }
        foreach (var project in projects)
        {
            project.WorkOrderNos = workOrderLookup.GetValueOrDefault(project.Id) ?? [];
            project.UnreadMessages = unreadLookup.GetValueOrDefault(project.Id);
        }
    }

    private static async Task ReplaceGroupWorkOrdersAsync(
        YfDbContext db, ulong groupId, IReadOnlyList<string> values, CancellationToken ct)
    {
        await db.ProjectGroupWorkOrders.Where(order => order.ProjectGroupId == groupId).ExecuteDeleteAsync(ct);
        db.ProjectGroupWorkOrders.AddRange(values.Select((value, index) => new ProjectGroupWorkOrder
        {
            ProjectGroupId = groupId,
            WorkOrderNo = value,
            SortNo = index,
        }));
        await db.SaveChangesAsync(ct);
    }

    private static async Task EnsureSupplierAsync(YfDbContext db, ulong supplierId, CancellationToken ct)
    {
        var status = await db.Suppliers.Where(supplier => supplier.Id == supplierId)
            .Select(supplier => supplier.Status).SingleOrDefaultAsync(ct);
        if (status is null) throw ApiException.BadRequest("供应商不存在");
        if (status != AccountStatuses.Active) throw ApiException.BadRequest("供应商已被禁用");
    }

    private static async Task EnsureGroupNameUniqueAsync(
        YfDbContext db, string name, ulong? excludeId, CancellationToken ct)
    {
        if (await db.ProjectGroups.AnyAsync(group => group.Name == name
                && (excludeId == null || group.Id != excludeId), ct))
            throw ApiException.Conflict("主项目名称已存在");
    }

    private static async Task EnsureProjectNameUniqueAsync(
        YfDbContext db, string name, ulong? excludeId, CancellationToken ct)
    {
        if (await db.Projects.AnyAsync(project => project.Name == name
                && (excludeId == null || project.Id != excludeId), ct))
            throw ApiException.Conflict("子项目名称已存在");
    }

    private static string[] NormalizeSubprojectNames(string?[]? values)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in values ?? [])
        {
            var name = (raw ?? string.Empty).Trim();
            if (name.Length == 0) continue;
            if (name.EnumerateRunes().Count() > 128) throw ApiException.BadRequest("子项目名称不能超过 128 个字符");
            if (seen.Add(name)) result.Add(name);
        }
        if (result.Count == 0) throw ApiException.BadRequest("请至少创建一个子项目");
        if (result.Count > 50) throw ApiException.BadRequest("单个主项目最多创建 50 个子项目");
        return result.ToArray();
    }

    private static ProjectService.ProjectMetadataInput Metadata(ProjectGroupRow group) => new(
        group.WorkOrderNos,
        group.MachineModel,
        group.RobotPartId,
        group.ResponsibleUserId,
        group.RobotOwnerName,
        group.SectionId,
        group.PriorityId,
        group.RobotTypeId,
        group.ExpectedCompletionDate);

    internal static string? NormalizeGroupStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return null;
        var value = status.Trim();
        if (value is not (ProjectStatuses.Draft or ProjectStatuses.InProgress
            or ProjectStatuses.Completed or ProjectStatuses.Terminated))
            throw ApiException.BadRequest("主项目状态不正确");
        return value;
    }

    private static DateOnly? ToDateOnly(DateTime? value) => value is null ? null : DateOnly.FromDateTime(value.Value);

    private static string? DateValue(DateTime? value) =>
        value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private sealed class GroupWorkOrderRow
    {
        public ulong ProjectGroupId { get; init; }
        public string WorkOrderNo { get; init; } = string.Empty;
    }

    private sealed record ProjectWorkOrderValue(ulong ProjectId, string WorkOrderNo);
    private sealed record UnreadValue(ulong ProjectId, long Count);
}
