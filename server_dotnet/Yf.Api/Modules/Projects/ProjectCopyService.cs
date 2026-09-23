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
    ProjectGroupStatusService groupStatus)
{
    internal async Task<ProjectCopyResponse> CopyAsync(MySqlConnection conn, CurrentUser actor, ulong sourceProjectId,
        ProjectCopyRequest request, string? ip, CancellationToken ct)
    {
        if (!actor.IsInternal) throw ApiException.Forbidden("仅内部用户可以复制项目");
        var targetName = ValidateName(request.Name);
        var snapshot = await CaptureSnapshotAsync(conn, actor, sourceProjectId, targetName, ct);
        var availableFiles = snapshot.Files.Where(file => file.Status == "AVAILABLE").ToArray();
        ulong totalBytes = 0;
        try { foreach (var file in availableFiles) totalBytes = checked(totalBytes + file.SizeBytes); }
        catch (OverflowException) { throw ApiException.Conflict("源项目文件总大小异常，无法复制"); }

        if (totalBytes > 0) FileStorage.EnsureFreeSpace(options.StorageRoot, totalBytes);
        var root = FileStorage.Root(options.StorageRoot);
        var prepared = new List<PreparedCopy>(availableFiles.Length);
        var committed = false;
        var commitOutcomeUnknown = false;
        ulong targetProjectId = 0;
        ulong copyId = 0;
        ProjectCopyResponse? response = null;
        try
        {
            foreach (var sourceFile in availableFiles)
                prepared.Add(await PreparePhysicalCopyAsync(root, snapshot.CopiedAt, sourceFile, ct));

            // File I/O stays outside the transaction. This short second transaction
            // fences permission changes (shared business gate) and proves the source snapshot is unchanged.
            await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
            await AccessService.LockBusinessAsync(conn, tx, ct);
            var current = await AccessService.RecheckActorAsync(conn, tx, actor, ct);
            await AccessService.RequirePermissionAsync(conn, tx, current, "project:list", ct);
            await AccessService.RequirePermissionAsync(conn, tx, current, "project:create", ct);
            await ProjectAccessService.RequireViewForValidatedActorAsync(conn, tx, current, sourceProjectId, true, ct);
            await using var db = EfDb.Use(conn, tx);
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
                RobotVendorId = currentGroup.RobotVendorId,
                RobotModelId = currentGroup.RobotModelId,
                ResponsibleUserId = currentGroup.ResponsibleUserId,
                SectionId = currentGroup.SectionId,
                PriorityId = currentGroup.PriorityId,
                ExpectedCompletionDate = ToDateOnly(currentGroup.ExpectedCompletionDate),
            };
            db.Projects.Add(targetProject);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException error) when (error.InnerException is MySqlException { Number: 1062 })
            { throw ApiException.Conflict("项目名称已存在"); }
            targetProjectId = targetProject.Id;

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
                UploaderId = item.Source.UploaderId,
                Direction = item.Source.Direction,
                OriginalName = item.Source.OriginalName,
                StoredName = item.StoredName,
                Ext = item.Source.Ext,
                SizeBytes = item.Source.SizeBytes,
                MimeType = item.Source.MimeType,
                Sha256 = item.Source.Sha256,
                StoragePath = item.StoragePath,
                Status = "AVAILABLE",
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
                    currentGroup.RobotVendorId, currentGroup.RobotModelId, currentGroup.ResponsibleUserId,
                    currentGroup.SectionId, currentGroup.PriorityId, currentGroup.ExpectedCompletionDate },
                fileCount = prepared.Count, totalBytes,
            }, ip, ct);
            await groupStatus.RecalculateAsync(conn, tx, currentGroup.Id, current.Id, targetProjectId, ct);

            var target = await LoadProjectAsync(db, targetProjectId, ct);
            target.HasCopyHistory = true;
            target.CopySourceProjectId = sourceProjectId;
            target.CopySourceProjectName = currentSource.Name;
            response = new ProjectCopyResponse(ProjectJson.Project(target), new ProjectCopyRecord(
                copyId, sourceProjectId, targetProjectId, prepared.Count, totalBytes, ProjectJson.Utc(createdAt)));
            try { await tx.CommitAsync(ct); committed = true; }
            catch (Exception commitError)
            {
                var outcome = await CheckCommitOutcomeAsync(targetProjectId, copyId);
                if (outcome == true) committed = true;
                else if (outcome == false) throw;
                else
                {
                    commitOutcomeUnknown = true;
                    throw new InvalidOperationException(
                        "项目复制提交结果未知；已保留候选文件以避免破坏可能已提交的数据，请按复制记录核对后处理。", commitError);
                }
            }
        }
        finally
        {
            if (!committed && !commitOutcomeUnknown)
                foreach (var item in prepared) TryDelete(item.TargetPath);
        }

        await PublishCommittedAsync(sourceProjectId);
        await PublishCommittedAsync(targetProjectId);
        return response!;
    }

    internal async Task<ProjectCopyHistoryResponse> HistoryAsync(MySqlConnection conn, CurrentUser actor, ulong projectId, CancellationToken ct)
    {
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        var current = await AccessService.LockActorAsync(conn, tx, actor, ct);
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
                TargetProjectId = copy.TargetProjectId,
                TargetName = copy.TargetProjectName,
                TargetSupplierId = target.SupplierId,
                TargetResponsibleUserId = target.ResponsibleUserId,
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
            var canViewRelated = current.IsInternal
                ? canViewAll || relatedResponsibleId == current.Id
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
        var current = await AccessService.LockActorAsync(conn, tx, actor, ct);
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
            row.SourceStatus != "AVAILABLE", row.TargetFileId, row.TargetFileName,
            row.TargetStatus != "AVAILABLE")).ToArray();
        await tx.CommitAsync(ct);
        return new PageResponse<FileCopyHistoryItem>(list, total, actualPage, size);
    }

    private static async Task<ProjectCopySnapshot> CaptureSnapshotAsync(MySqlConnection conn, CurrentUser actor,
        ulong sourceProjectId, string targetName, CancellationToken ct)
    {
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        var current = await AccessService.LockActorAsync(conn, tx, actor, ct);
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
        var copiedAt = await DatabaseUtcNowAsync(db, ct);
        await tx.CommitAsync(ct);
        return new(source, group, files, copiedAt);
    }

    private static async Task<PreparedCopy> PreparePhysicalCopyAsync(string root, DateTime copiedAt,
        CopyFileRow sourceFile, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sourceFile.Sha256) || sourceFile.Sha256.Length != 64)
            throw ApiException.Conflict($"源文件“{sourceFile.OriginalName}”缺少完整性校验值，无法复制");
        string sourcePath;
        try { sourcePath = FileStorage.ResolveExistingFile(root, Path.Combine(root, sourceFile.StoragePath), ct); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException or InvalidOperationException)
        { throw ApiException.Conflict($"源文件“{sourceFile.OriginalName}”缺失或存储路径异常，无法复制"); }
        var storedName = $"{Guid.NewGuid():D}.{sourceFile.Ext}";
        var targetPath = FileStorage.FinalPath(root, copiedAt, storedName);
        var directory = Path.GetDirectoryName(targetPath) ?? throw new InvalidOperationException("存储目录无效");
        Directory.CreateDirectory(directory);
        FileStorage.ResolveExisting(root, directory, false, ct);
        try
        {
            var hash = await FileStorage.HashAndCopyAsync([sourcePath], targetPath, sourceFile.SizeBytes, ct);
            if (hash.Bytes != sourceFile.SizeBytes || !hash.Sha256.Equals(sourceFile.Sha256, StringComparison.OrdinalIgnoreCase))
                throw ApiException.Conflict($"源文件“{sourceFile.OriginalName}”完整性校验失败，项目未复制");
            return new(sourceFile, storedName, Path.GetRelativePath(root, targetPath).Replace(Path.DirectorySeparatorChar, '/'), targetPath);
        }
        catch { TryDelete(targetPath); throw; }
    }

    private async Task<bool?> CheckCommitOutcomeAsync(ulong targetProjectId, ulong copyId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await using var verification = await database.OpenAsync(timeout.Token);
            await using var db = EfDb.Use(verification);
            return await db.ProjectCopies.AnyAsync(copy => copy.Id == copyId
                && copy.TargetProjectId == targetProjectId, timeout.Token);
        }
        catch { return null; }
    }

    private static async Task ValidateSourceAsync(YfDbContext db, ProjectRow source, CancellationToken ct)
    {
        if (source.WorkOrderNos.Length == 0 || string.IsNullOrWhiteSpace(source.MachineModel)
            || source.RobotVendorId is null or 0 || source.RobotModelId is null or 0
            || source.ResponsibleUserId is null or 0 || source.SectionId is null or 0
            || source.PriorityId is null or 0 || source.ExpectedCompletionDate is null)
            throw ApiException.Conflict("源项目资料不完整，请先补齐必填信息后再复制");
        if (!await ValidMetadataAsync(db, source.SupplierId, source.ResponsibleUserId.Value, source.SectionId.Value,
                source.RobotVendorId.Value, source.RobotModelId.Value, source.PriorityId.Value, ct))
            throw ApiException.Conflict("源项目关联资料已失效，请先更新负责人、课别、供应商或数据字典后再复制");
    }

    private static async Task ValidateGroupAsync(YfDbContext db, CopyGroupRow group, CancellationToken ct)
    {
        if (group.WorkOrderNos.Length == 0 || string.IsNullOrWhiteSpace(group.MachineModel)
            || group.RobotVendorId is null or 0 || group.RobotModelId is null or 0
            || group.ResponsibleUserId is null or 0 || group.SectionId is null or 0
            || group.PriorityId is null or 0 || group.ExpectedCompletionDate is null)
            throw ApiException.Conflict("主项目资料不完整，请先补齐必填信息后再复制");
        if (!await ValidMetadataAsync(db, group.SupplierId, group.ResponsibleUserId.Value, group.SectionId.Value,
                group.RobotVendorId.Value, group.RobotModelId.Value, group.PriorityId.Value, ct))
            throw ApiException.Conflict("主项目关联资料已失效，请先更新负责人、课别、供应商或数据字典后再复制");
    }

    private static async Task<bool> ValidMetadataAsync(YfDbContext db, ulong supplierId, ulong ownerId, ulong sectionId,
        ulong vendorId, ulong modelId, ulong priorityId, CancellationToken ct)
    {
        if (!await db.Suppliers.AnyAsync(supplier => supplier.Id == supplierId && supplier.Status == "ACTIVE", ct))
            return false;
        if (!await db.Users.AnyAsync(owner => owner.Id == ownerId && owner.UserType == "INTERNAL"
                && owner.Status == "ACTIVE" && owner.DepartmentId == sectionId, ct))
            return false;
        if (!await db.Departments.AnyAsync(section => section.Id == sectionId && section.Kind == "SECTION"
                && section.Status == "ACTIVE"
                && (section.ParentId == null || db.Departments.Any(parent => parent.Id == section.ParentId
                    && parent.Kind == "DEPARTMENT" && parent.Status == "ACTIVE"
                    && (parent.ParentId == null || db.Departments.Any(root => root.Id == parent.ParentId
                        && root.Kind == "DIVISION" && root.Status == "ACTIVE" && root.ParentId == null)))), ct))
            return false;
        if (!await (from userRole in db.UserRoles
                    join role in db.Roles on userRole.RoleId equals role.Id
                    join rolePermission in db.RolePermissions on role.Id equals rolePermission.RoleId
                    join permission in db.Permissions on rolePermission.PermissionId equals permission.Id
                    where userRole.UserId == ownerId && role.Status == "ACTIVE" && permission.Code == "project:list"
                    select permission.Id).AnyAsync(ct))
            return false;
        if (!await db.ProjectDictionaries.AnyAsync(vendor => vendor.Id == vendorId
                && vendor.Type == ProjectDictionaryTypes.RobotVendor && vendor.Status == "ACTIVE", ct))
            return false;
        if (!await db.ProjectDictionaries.AnyAsync(model => model.Id == modelId
                && model.Type == ProjectDictionaryTypes.RobotModel && model.ParentId == vendorId && model.Status == "ACTIVE", ct))
            return false;
        return await db.ProjectDictionaries.AnyAsync(priority => priority.Id == priorityId
            && priority.Type == ProjectDictionaryTypes.Priority && priority.Status == "ACTIVE", ct);
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
            RobotVendorId = group.RobotVendorId,
            RobotModelId = group.RobotModelId,
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
            file.Ext, file.SizeBytes, file.MimeType, file.Sha256, file.StoragePath, file.Status)).ToArray();
    }

    private static async Task<ulong> ActiveUploadCountAsync(YfDbContext db, ulong projectId, CancellationToken ct) =>
        (ulong)await db.UploadSessions.LongCountAsync(upload => upload.ProjectId == projectId
            && (upload.Status == "UPLOADING" || upload.Status == "MERGING"), ct);

    private static bool SameProjectSnapshot(ProjectRow a, ProjectRow b) => a.Id == b.Id
        && a.ProjectGroupId == b.ProjectGroupId && a.Name == b.Name
        && a.Description == b.Description && a.SupplierId == b.SupplierId && a.Status == b.Status
        && a.ConfirmSide == b.ConfirmSide && a.UpdatedAt == b.UpdatedAt && a.MachineModel == b.MachineModel
        && a.RobotVendorId == b.RobotVendorId && a.RobotModelId == b.RobotModelId
        && a.ResponsibleUserId == b.ResponsibleUserId && a.SectionId == b.SectionId && a.PriorityId == b.PriorityId
        && a.ExpectedCompletionDate == b.ExpectedCompletionDate && a.WorkOrderNos.SequenceEqual(b.WorkOrderNos, StringComparer.Ordinal);

    private static bool SameGroupSnapshot(CopyGroupRow a, CopyGroupRow b) => a.Id == b.Id
        && a.SupplierId == b.SupplierId && a.Status == b.Status && a.UpdatedAt == b.UpdatedAt
        && a.MachineModel == b.MachineModel && a.RobotVendorId == b.RobotVendorId
        && a.RobotModelId == b.RobotModelId && a.ResponsibleUserId == b.ResponsibleUserId
        && a.SectionId == b.SectionId && a.PriorityId == b.PriorityId
        && a.ExpectedCompletionDate == b.ExpectedCompletionDate
        && a.WorkOrderNos.SequenceEqual(b.WorkOrderNos, StringComparer.Ordinal);

    private static bool SameFileSnapshot(CopyFileRow[] a, CopyFileRow[] b) =>
        a.Length == b.Length && a.Zip(b).All(pair => pair.First == pair.Second);

    private static async Task EnsureNameUniqueAsync(YfDbContext db, string name, CancellationToken ct)
    {
        if (await db.Projects.AnyAsync(project => project.Name == name, ct))
            throw ApiException.Conflict("项目名称已存在");
    }

    private static string ValidateName(string? value)
    {
        var name = (value ?? string.Empty).Trim();
        if (name.Length == 0 || name.EnumerateRunes().Count() > 128)
            throw ApiException.BadRequest("新项目名称需为 1~128 个字符");
        return name;
    }

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
        await DbClock.UtcNowAsync(db, ct);

    private static DateOnly? ToDateOnly(DateTime? value) => value is null ? null : DateOnly.FromDateTime(value.Value);

    private static object Snapshot(ProjectRow project) => new { id = project.Id, project.Name, project.Status,
        project.SupplierId, project.WorkOrderNos, project.MachineModel, project.RobotVendorId, project.RobotModelId,
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
        catch { }
    }

    private static void TryDelete(string path) { try { File.Delete(path); } catch { } }

    private sealed record ProjectCopySnapshot(ProjectRow Project, CopyGroupRow Group, CopyFileRow[] Files, DateTime CopiedAt);
    private sealed class CopyGroupRow
    {
        public ulong Id { get; init; }
        public ulong SupplierId { get; init; }
        public string Status { get; init; } = string.Empty;
        public string[] WorkOrderNos { get; set; } = [];
        public string? MachineModel { get; init; }
        public ulong? RobotVendorId { get; init; }
        public ulong? RobotModelId { get; init; }
        public ulong? ResponsibleUserId { get; init; }
        public ulong? SectionId { get; init; }
        public ulong? PriorityId { get; init; }
        public DateTime? ExpectedCompletionDate { get; init; }
        public DateTime UpdatedAt { get; init; }
    }

    private sealed record PreparedCopy(CopyFileRow Source, string StoredName, string StoragePath, string TargetPath);
    private sealed record CopyFileRow(ulong Id, ulong UploaderId, string Direction, string OriginalName, string Ext,
        ulong SizeBytes, string? MimeType, string? Sha256, string StoragePath, string Status);
    private sealed class CopyHistoryRow
    {
        public ulong CopyId { get; init; }
        public ulong SourceProjectId { get; init; }
        public string SourceName { get; init; } = string.Empty;
        public ulong SourceSupplierId { get; init; }
        public ulong? SourceResponsibleUserId { get; init; }
        public ulong TargetProjectId { get; init; }
        public string TargetName { get; init; } = string.Empty;
        public ulong TargetSupplierId { get; init; }
        public ulong? TargetResponsibleUserId { get; init; }
        public string CopiedByName { get; init; } = string.Empty;
        public ulong FileCount { get; init; }
        public ulong TotalBytes { get; init; }
        public DateTime CreatedAt { get; init; }
    }

    private sealed record ProjectCopyIds(ulong SourceProjectId, ulong TargetProjectId);
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
