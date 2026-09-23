using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Yf.Api.Infrastructure;
using Yf.Api.Infrastructure.Entities;

namespace Yf.Api.Modules.Projects;

internal sealed class ProjectGroupService(
    AuditService audit,
    ProjectGroupStatusService groupStatus)
{
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
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        var current = await AccessService.LockActorAsync(conn, tx, actor, ct);
        await using var db = EfDb.Use(conn, tx);
        var query = await ProjectGroupAccessService.VisibleQueryAsync(db, current, ct);
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var value = keyword.Trim();
            query = query.Where(group => group.Name.Contains(value));
        }
        if (!string.IsNullOrWhiteSpace(status))
        {
            var value = status.Trim();
            query = query.Where(group => group.Status == value);
        }
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
        var current = await AccessService.LockActorAsync(conn, tx, actor, ct);
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
        var metadata = ProjectService.NormalizeMetadata(request);
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        await AccessService.LockBusinessAsync(conn, tx, ct);
        var current = await AccessService.RecheckActorAsync(conn, tx, actor, ct);
        await AccessService.RequirePermissionAsync(conn, tx, current, "project:list", ct);
        await AccessService.RequirePermissionAsync(conn, tx, current, "project:create", ct);
        await using var db = EfDb.Use(conn, tx);
        await EnsureSupplierAsync(db, request.SupplierId, ct);
        await EnsureGroupNameUniqueAsync(db, name, null, ct);
        foreach (var childName in childNames) await EnsureProjectNameUniqueAsync(db, childName, null, ct);
        metadata = await ProjectService.ValidateMetadataAsync(conn, tx, metadata, null, ct);

        var group = new ProjectGroup
        {
            Name = name,
            Description = request.Description,
            SupplierId = request.SupplierId,
            Status = ProjectStatuses.Draft,
            CreatedBy = current.Id,
            MachineModel = metadata.MachineModel,
            RobotVendorId = metadata.RobotVendorId,
            RobotModelId = metadata.RobotModelId,
            ResponsibleUserId = metadata.ResponsibleUserId,
            SectionId = metadata.SectionId,
            PriorityId = metadata.PriorityId,
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
        { throw ApiException.Conflict("项目名称已存在"); }
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

        for (var index = 0; index < children.Length; index++)
            await audit.WriteAsync(conn, tx, current.Id, "PROJECT_CREATE", "project", children[index].Id, new
            {
                name = childNames[index],
                projectGroupId = group.Id,
                projectGroupName = name,
                inheritedFromMainProject = true,
            }, ip, ct);
        await audit.WriteAsync(conn, tx, current.Id, "PROJECT_GROUP_CREATE", "project_group", group.Id, new
        {
            name,
            request.SupplierId,
            metadata.WorkOrderNos,
            metadata.MachineModel,
            metadata.RobotVendorId,
            metadata.RobotModelId,
            metadata.ResponsibleUserId,
            metadata.SectionId,
            metadata.PriorityId,
            metadata.ExpectedCompletionDate,
            subprojectIds = children.Select(child => child.Id).ToArray(),
            subprojectNames = childNames,
        }, ip, ct);
        var result = ProjectJson.ProjectGroup(await LoadGroupAsync(db, group.Id, current.Id, ct));
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
        var metadata = ProjectService.NormalizeMetadata(request);
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
        var before = await LoadGroupAsync(db, groupId, current.Id, ct);
        await EnsureGroupNameUniqueAsync(db, name, groupId, ct);
        metadata = await ProjectService.ValidateMetadataAsync(conn, tx, metadata, new ProjectRow
        {
            RobotVendorId = before.RobotVendorId,
            RobotModelId = before.RobotModelId,
            PriorityId = before.PriorityId,
        }, ct);

        try
        {
            await db.ProjectGroups.Where(group => group.Id == groupId).ExecuteUpdateAsync(setters => setters
                .SetProperty(group => group.Name, name)
                .SetProperty(group => group.Description, request.Description)
                .SetProperty(group => group.MachineModel, metadata.MachineModel)
                .SetProperty(group => group.RobotVendorId, metadata.RobotVendorId)
                .SetProperty(group => group.RobotModelId, metadata.RobotModelId)
                .SetProperty(group => group.ResponsibleUserId, metadata.ResponsibleUserId)
                .SetProperty(group => group.SectionId, metadata.SectionId)
                .SetProperty(group => group.PriorityId, metadata.PriorityId)
                .SetProperty(group => group.ExpectedCompletionDate, ToDateOnly(metadata.ExpectedCompletionDate)), ct);
        }
        catch (DbUpdateException error) when (error.InnerException is MySqlException { Number: 1062 })
        { throw ApiException.Conflict("主项目名称已存在"); }

        var childIds = await db.Projects
            .Where(project => project.ProjectGroupId == groupId
                && (project.Status != ProjectStatuses.Completed
                    || project.ResponsibleUserId != metadata.ResponsibleUserId
                    || project.SectionId != metadata.SectionId))
            .OrderBy(project => project.Id).Select(project => project.Id).ToArrayAsync(ct);
        await db.Projects.Where(project => project.ProjectGroupId == groupId
                && (project.ResponsibleUserId != metadata.ResponsibleUserId
                    || project.SectionId != metadata.SectionId))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(project => project.ResponsibleUserId, metadata.ResponsibleUserId)
                .SetProperty(project => project.SectionId, metadata.SectionId), ct);
        await db.Projects.Where(project => project.ProjectGroupId == groupId
                && project.Status != ProjectStatuses.Completed)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(project => project.MachineModel, metadata.MachineModel)
                .SetProperty(project => project.RobotVendorId, metadata.RobotVendorId)
                .SetProperty(project => project.RobotModelId, metadata.RobotModelId)
                .SetProperty(project => project.ResponsibleUserId, metadata.ResponsibleUserId)
                .SetProperty(project => project.SectionId, metadata.SectionId)
                .SetProperty(project => project.PriorityId, metadata.PriorityId)
                .SetProperty(project => project.ExpectedCompletionDate, ToDateOnly(metadata.ExpectedCompletionDate)), ct);
        await ReplaceGroupWorkOrdersAsync(db, groupId, metadata.WorkOrderNos, ct);
        await db.ProjectWorkOrders.Where(order => db.Projects.Any(project => project.Id == order.ProjectId
                && project.ProjectGroupId == groupId && project.Status != ProjectStatuses.Completed))
            .ExecuteDeleteAsync(ct);
        var mutableProjectIds = await db.Projects.Where(project => project.ProjectGroupId == groupId
                && project.Status != ProjectStatuses.Completed)
            .Select(project => project.Id).ToArrayAsync(ct);
        db.ProjectWorkOrders.AddRange(mutableProjectIds.SelectMany(projectId => metadata.WorkOrderNos.Select((value, index) =>
            new ProjectWorkOrder { ProjectId = projectId, WorkOrderNo = value, SortNo = index })));
        await db.SaveChangesAsync(ct);

        foreach (var childId in childIds)
            await audit.WriteAsync(conn, tx, current.Id, "PROJECT_UPDATE", "project", childId, new
            {
                projectGroupId = groupId,
                inheritedFromMainProject = true,
                changedByMainProject = true,
            }, ip, ct);
        await audit.WriteAsync(conn, tx, current.Id, "PROJECT_GROUP_UPDATE", "project_group", groupId, new
        {
            name,
            changes = AuditChange.OnlyChanged(
                new("name", "主项目名称", before.Name, name),
                new("description", "项目说明", before.Description, request.Description),
                new("workOrderNos", "工令号", before.WorkOrderNos, metadata.WorkOrderNos),
                new("machineModel", "机型", before.MachineModel, metadata.MachineModel),
                new("robotVendorId", "Robot 厂商", before.RobotVendorId, metadata.RobotVendorId),
                new("robotModelId", "Robot 型号", before.RobotModelId, metadata.RobotModelId),
                new("responsibleUserId", "负责人", before.ResponsibleUserId, metadata.ResponsibleUserId),
                new("sectionId", "课别", before.SectionId, metadata.SectionId),
                new("priorityId", "优先级", before.PriorityId, metadata.PriorityId),
                new("expectedCompletionDate", "预计完成日期",
                    DateValue(before.ExpectedCompletionDate), DateValue(metadata.ExpectedCompletionDate))),
        }, ip, ct);
        var result = ProjectJson.ProjectGroup(await LoadGroupAsync(db, groupId, current.Id, ct));
        await tx.CommitAsync(ct);
        return result;
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
        var group = await LoadGroupAsync(db, groupId, current.Id, ct);
        var metadata = Metadata(group);
        var child = NewChild(groupId, name, request.Description, group.SupplierId, current.Id, metadata);
        db.Projects.Add(child);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException error) when (error.InnerException is MySqlException { Number: 1062 })
        { throw ApiException.Conflict("项目名称已存在"); }
        AddChildRecords(db, [child], metadata.WorkOrderNos, current.Id, "CREATE", await DbClock.UtcNowAsync(db, ct, 3));
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(conn, tx, current.Id, "PROJECT_CREATE", "project", child.Id, new
        {
            name,
            projectGroupId = groupId,
            projectGroupName = group.Name,
            inheritedFromMainProject = true,
        }, ip, ct);
        await groupStatus.RecalculateAsync(conn, tx, groupId, current.Id, child.Id, ct);
        var result = ProjectJson.Project(await LoadChildAsync(db, child.Id, current.Id, ct));
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
        var group = await LoadGroupAsync(db, groupId, current.Id, ct);
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
        RobotVendorId = metadata.RobotVendorId,
        RobotModelId = metadata.RobotModelId,
        ResponsibleUserId = metadata.ResponsibleUserId,
        SectionId = metadata.SectionId,
        PriorityId = metadata.PriorityId,
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

    // Unread counts are loaded per page by LoadGroupUnreadAsync: a correlated per-group count here would
    // scan every message once per group.
    private static IQueryable<ProjectGroupRow> GroupRows(
        YfDbContext db,
        IQueryable<ProjectGroup> query) => query.Select(group => new ProjectGroupRow
    {
        Id = group.Id,
        Name = group.Name,
        Description = group.Description,
        SupplierId = group.SupplierId,
        SupplierName = db.Suppliers.Where(supplier => supplier.Id == group.SupplierId)
            .Select(supplier => supplier.Name).FirstOrDefault(),
        Status = group.Status,
        CreatedBy = group.CreatedBy,
        CreatedByName = db.Users.Where(user => user.Id == group.CreatedBy)
            .Select(user => user.RealName).FirstOrDefault(),
        MachineModel = group.MachineModel,
        RobotVendorId = group.RobotVendorId,
        RobotVendorName = db.ProjectDictionaries.Where(item => item.Id == group.RobotVendorId)
            .Select(item => item.Name).FirstOrDefault(),
        RobotModelId = group.RobotModelId,
        RobotModelName = db.ProjectDictionaries.Where(item => item.Id == group.RobotModelId)
            .Select(item => item.Name).FirstOrDefault(),
        ResponsibleUserId = group.ResponsibleUserId,
        ResponsibleUserEmployeeNo = db.Users.Where(user => user.Id == group.ResponsibleUserId)
            .Select(user => user.EmployeeNo).FirstOrDefault(),
        ResponsibleUserName = db.Users.Where(user => user.Id == group.ResponsibleUserId)
            .Select(user => user.RealName).FirstOrDefault(),
        SectionId = group.SectionId,
        SectionName = db.Departments.Where(section => section.Id == group.SectionId && section.Kind == "SECTION")
            .Select(section => section.Name).FirstOrDefault(),
        PriorityId = group.PriorityId,
        PriorityName = db.ProjectDictionaries.Where(item => item.Id == group.PriorityId)
            .Select(item => item.Name).FirstOrDefault(),
        ExpectedCompletionDate = group.ExpectedCompletionDate.HasValue
            ? group.ExpectedCompletionDate.Value.ToDateTime(TimeOnly.MinValue) : null,
        CompletedAt = group.CompletedAt,
        CreatedAt = group.CreatedAt,
        UpdatedAt = group.UpdatedAt,
        SubprojectCount = (ulong)db.Projects.LongCount(project => project.ProjectGroupId == group.Id),
        CompletedCount = (ulong)db.Projects.LongCount(project => project.ProjectGroupId == group.Id
            && project.Status == ProjectStatuses.Completed),
        PendingCount = (ulong)db.Projects.LongCount(project => project.ProjectGroupId == group.Id
            && project.Status == ProjectStatuses.PendingConfirmation),
        TerminatedCount = (ulong)db.Projects.LongCount(project => project.ProjectGroupId == group.Id
            && project.Status == ProjectStatuses.Terminated),
    });

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
        YfDbContext db, ulong groupId, ulong userId, CancellationToken ct)
    {
        var row = await GroupRows(db, db.ProjectGroups.Where(group => group.Id == groupId))
            .SingleOrDefaultAsync(ct) ?? throw ApiException.NotFound();
        await LoadWorkOrdersAsync(db, [row], ct);
        await LoadGroupUnreadAsync(db, [row], userId, ct);
        return row;
    }

    private static async Task<ProjectRow> LoadChildAsync(
        YfDbContext db, ulong projectId, ulong userId, CancellationToken ct)
    {
        var row = await ProjectQueries.Rows(db).SingleOrDefaultAsync(project => project.Id == projectId, ct)
            ?? throw ApiException.NotFound();
        await LoadProjectExtrasAsync(db, [row], userId, ct);
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
        YfDbContext db, IReadOnlyCollection<ProjectRow> projects, ulong userId, CancellationToken ct)
    {
        if (projects.Count == 0) return;
        var ids = projects.Select(project => project.Id).ToArray();
        var workOrders = await db.ProjectWorkOrders.Where(order => Enumerable.Contains(ids, order.ProjectId))
            .OrderBy(order => order.ProjectId).ThenBy(order => order.SortNo).ThenBy(order => order.Id)
            .Select(order => new ProjectWorkOrderValue(order.ProjectId, order.WorkOrderNo)).ToArrayAsync(ct);
        var workOrderLookup = workOrders.GroupBy(row => row.ProjectId)
            .ToDictionary(group => group.Key, group => group.Select(row => row.WorkOrderNo).ToArray());
        var cutoff = await UnreadWindow.CutoffAsync(db, ct);
        var unreadRows = await db.Messages.Where(message => Enumerable.Contains(ids, message.ProjectId)
                && message.Status == "NORMAL" && message.SenderId != userId && message.CreatedAt >= cutoff
                && !db.MessageReads.Any(receipt => receipt.MessageId == message.Id && receipt.UserId == userId))
            .GroupBy(message => message.ProjectId)
            .Select(group => new UnreadValue(group.Key, group.LongCount())).ToArrayAsync(ct);
        var unreadLookup = unreadRows.ToDictionary(row => row.ProjectId, row => (ulong)row.Count);
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
        if (status != "ACTIVE") throw ApiException.BadRequest("供应商已被禁用");
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
            throw ApiException.Conflict("项目名称已存在");
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
        group.RobotVendorId,
        group.RobotModelId,
        group.ResponsibleUserId,
        group.SectionId,
        group.PriorityId,
        group.ExpectedCompletionDate);

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
