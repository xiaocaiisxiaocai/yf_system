using System.Text;
using Dapper;
using MySqlConnector;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Files;

namespace Yf.Api.Modules.Projects;

internal sealed class ProjectCopyService(
    AppDb database,
    AppOptions options,
    AuditService audit,
    IProjectRealtimePublisher realtime,
    ProjectGroupStatusService groupStatus)
{
    internal async Task<object> CopyAsync(MySqlConnection conn, CurrentUser actor, ulong sourceProjectId,
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
        object? response = null;
        try
        {
            foreach (var sourceFile in availableFiles)
                prepared.Add(await PreparePhysicalCopyAsync(root, snapshot.CopiedAt, sourceFile, ct));

            // The expensive file I/O is complete. This second, short transaction
            // fences management changes and proves the source snapshot is unchanged.
            await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
            await AccessService.LockManagementAsync(conn, tx, ct);
            var current = await AccessService.RecheckActorAsync(conn, tx, actor, ct);
            await AccessService.RequirePermissionAsync(conn, tx, current, "project:list", ct);
            await AccessService.RequirePermissionAsync(conn, tx, current, "project:create", ct);
            await ProjectAccessService.RequireViewForValidatedActorAsync(conn, tx, current, sourceProjectId, true, ct);
            var currentSource = await LoadProjectAsync(conn, tx, sourceProjectId, ct);
            var currentGroup = await LoadGroupAsync(conn, tx, currentSource.ProjectGroupId, ct);
            var currentFiles = await LoadFilesForUpdateAsync(conn, tx, sourceProjectId, ct);
            if (!SameProjectSnapshot(snapshot.Project, currentSource)
                || !SameGroupSnapshot(snapshot.Group, currentGroup)
                || !SameFileSnapshot(snapshot.Files, currentFiles)
                || await ActiveUploadCountAsync(conn, tx, sourceProjectId, ct) != 0)
                throw ApiException.Conflict("源项目或文件已发生变化，请刷新后重新复制");
            await ValidateSourceAsync(conn, tx, currentSource, ct);
            await ValidateGroupAsync(conn, tx, currentGroup, ct);
            if (currentGroup.Status is ProjectStatuses.Completed or ProjectStatuses.Terminated)
                throw ApiException.Conflict("主项目已结束，不能复制子项目");
            await EnsureNameUniqueAsync(conn, tx, targetName, ct);
            var createdAt = await conn.ExecuteScalarAsync<DateTime>(new CommandDefinition(
                "SELECT UTC_TIMESTAMP(6)", transaction: tx, cancellationToken: ct));

            try
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO projects(project_group_id,name,description,supplier_id,status,confirm_side,created_by,created_at,updated_at,
                        machine_model,robot_vendor_id,robot_model_id,responsible_user_id,section_id,priority_id,expected_completion_date)
                    VALUES(@ProjectGroupId,@TargetName,@Description,@SupplierId,'DRAFT',NULL,@ActorId,@CreatedAt,@CreatedAt,
                        @MachineModel,@RobotVendorId,@RobotModelId,@ResponsibleUserId,@SectionId,@PriorityId,@ExpectedCompletionDate)
                    """,
                    new { ProjectGroupId = currentGroup.Id, TargetName = targetName, currentSource.Description,
                        currentGroup.SupplierId, ActorId = current.Id, CreatedAt = createdAt,
                        currentGroup.MachineModel, currentGroup.RobotVendorId, currentGroup.RobotModelId,
                        currentGroup.ResponsibleUserId, currentGroup.SectionId, currentGroup.PriorityId,
                        currentGroup.ExpectedCompletionDate }, tx, cancellationToken: ct));
            }
            catch (MySqlException error) when (error.Number == 1062) { throw ApiException.Conflict("项目名称已存在"); }
            targetProjectId = await LastInsertIdAsync(conn, tx, ct);
            for (var index = 0; index < currentGroup.WorkOrderNos.Length; index++)
                await conn.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO project_work_orders(project_id,work_order_no,sort_no,created_at) VALUES(@ProjectId,@WorkOrderNo,@SortNo,@CreatedAt)",
                    new { ProjectId = targetProjectId, WorkOrderNo = currentGroup.WorkOrderNos[index], SortNo = index,
                        CreatedAt = createdAt }, tx, cancellationToken: ct));

            var actorName = await conn.QuerySingleAsync<string>(new CommandDefinition(
                "SELECT real_name FROM users WHERE id=@Id", new { current.Id }, tx, cancellationToken: ct));
            await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO project_copies(source_project_id,target_project_id,source_project_name,target_project_name,
                    copied_by,copied_by_name,file_count,total_bytes,created_at)
                VALUES(@SourceProjectId,@TargetProjectId,@SourceName,@TargetName,@ActorId,@ActorName,@FileCount,@TotalBytes,@CreatedAt)
                """,
                new { SourceProjectId = sourceProjectId, TargetProjectId = targetProjectId, SourceName = currentSource.Name,
                    TargetName = targetName, ActorId = current.Id, ActorName = actorName, FileCount = prepared.Count,
                    TotalBytes = totalBytes, CreatedAt = createdAt }, tx, cancellationToken: ct));
            copyId = await LastInsertIdAsync(conn, tx, ct);

            foreach (var item in prepared)
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO files(project_id,uploader_id,direction,original_name,stored_name,ext,size_bytes,mime_type,
                        sha256,storage_path,status,deleted_at,created_at)
                    VALUES(@ProjectId,@UploaderId,@Direction,@OriginalName,@StoredName,@Ext,@SizeBytes,@MimeType,
                        @Sha256,@StoragePath,'AVAILABLE',NULL,@CreatedAt)
                    """,
                    new { ProjectId = targetProjectId, item.Source.UploaderId, item.Source.Direction,
                        item.Source.OriginalName, item.StoredName, item.Source.Ext, item.Source.SizeBytes,
                        item.Source.MimeType, item.Source.Sha256, item.StoragePath, CreatedAt = createdAt },
                    tx, cancellationToken: ct));
                var targetFileId = await LastInsertIdAsync(conn, tx, ct);
                await conn.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO file_copy_refs(copy_id,source_file_id,target_file_id,source_file_name,target_file_name,created_at)
                    VALUES(@CopyId,@SourceFileId,@TargetFileId,@SourceFileName,@TargetFileName,@CreatedAt)
                    """,
                    new { CopyId = copyId, SourceFileId = item.Source.Id, TargetFileId = targetFileId,
                        SourceFileName = item.Source.OriginalName, TargetFileName = item.Source.OriginalName,
                        CreatedAt = createdAt }, tx, cancellationToken: ct));
            }

            await conn.ExecuteAsync(new CommandDefinition(
                "INSERT INTO project_status_logs(project_id,from_status,to_status,action,operator_id,confirm_side,reason,created_at) VALUES(@ProjectId,NULL,'DRAFT','COPY',@ActorId,NULL,NULL,@CreatedAt)",
                new { ProjectId = targetProjectId, ActorId = current.Id, CreatedAt = createdAt }, tx, cancellationToken: ct));
            await InsertActivitiesAsync(conn, tx, sourceProjectId, targetProjectId, copyId, current.Id, actorName,
                prepared.Count, createdAt, ct);
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

            var target = await LoadProjectAsync(conn, tx, targetProjectId, ct);
            target.HasCopyHistory = true;
            target.CopySourceProjectId = sourceProjectId;
            target.CopySourceProjectName = currentSource.Name;
            response = new { project = ProjectJson.Project(target), copy = new { copyId, sourceProjectId, targetProjectId,
                fileCount = prepared.Count, totalBytes, createdAt = ProjectJson.Utc(createdAt) } };
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

    internal async Task<object> HistoryAsync(MySqlConnection conn, CurrentUser actor, ulong projectId, CancellationToken ct)
    {
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        var current = await AccessService.LockActorAsync(conn, tx, actor, ct);
        await ProjectAccessService.RequireViewForValidatedActorAsync(conn, tx, current, projectId, false, ct);
        var rows = (await conn.QueryAsync<CopyHistoryRow>(new CommandDefinition(
            """
            SELECT pc.id AS CopyId,pc.source_project_id AS SourceProjectId,source.name AS SourceName,
                   source.supplier_id AS SourceSupplierId,source.responsible_user_id AS SourceResponsibleUserId,
                   pc.target_project_id AS TargetProjectId,target.name AS TargetName,
                   target.supplier_id AS TargetSupplierId,target.responsible_user_id AS TargetResponsibleUserId,
                   pc.copied_by_name AS CopiedByName,pc.file_count AS FileCount,
                   pc.total_bytes AS TotalBytes,pc.created_at AS CreatedAt
            FROM project_copies pc JOIN projects source ON source.id=pc.source_project_id
            JOIN projects target ON target.id=pc.target_project_id
            WHERE pc.source_project_id=@ProjectId OR pc.target_project_id=@ProjectId
            ORDER BY pc.created_at DESC,pc.id DESC
            """, new { ProjectId = projectId }, tx, cancellationToken: ct))).AsList();
        object? source = null;
        var copies = new List<object>();
        var restricted = false;
        var canViewAll = current.IsInternal
            && await ProjectAccessService.HasPermissionAsync(conn, tx, current.Id, "project:view_all", ct);
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
            if (row.TargetProjectId == projectId) source = item; else copies.Add(item);
        }
        await tx.CommitAsync(ct);
        return new { source, copies, hasRestrictedRelations = restricted };
    }

    internal async Task<object> FileHistoryAsync(MySqlConnection conn, CurrentUser actor, ulong copyId,
        ulong page, ulong pageSize, CancellationToken ct)
    {
        var (actualPage, size) = ProjectJson.ClampPage(page, pageSize);
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        var current = await AccessService.LockActorAsync(conn, tx, actor, ct);
        var relation = await conn.QuerySingleOrDefaultAsync<ProjectCopyIds>(new CommandDefinition(
            "SELECT source_project_id AS SourceProjectId,target_project_id AS TargetProjectId FROM project_copies WHERE id=@CopyId",
            new { CopyId = copyId }, tx, cancellationToken: ct));
        if (relation is null || !await CanViewAsync(conn, tx, current, relation.SourceProjectId, ct)
            || !await CanViewAsync(conn, tx, current, relation.TargetProjectId, ct)) throw ApiException.NotFound();
        var total = await conn.ExecuteScalarAsync<ulong>(new CommandDefinition(
            "SELECT COUNT(*) FROM file_copy_refs WHERE copy_id=@CopyId", new { CopyId = copyId }, tx, cancellationToken: ct));
        var list = (await conn.QueryAsync<FileCopyHistoryRow>(new CommandDefinition(
            """
            SELECT fcr.source_file_id AS SourceFileId,fcr.source_file_name AS SourceFileName,
                   fcr.target_file_id AS TargetFileId,fcr.target_file_name AS TargetFileName,
                   source.status AS SourceStatus,target.status AS TargetStatus
            FROM file_copy_refs fcr LEFT JOIN files source ON source.id=fcr.source_file_id
            LEFT JOIN files target ON target.id=fcr.target_file_id
            WHERE fcr.copy_id=@CopyId ORDER BY fcr.id LIMIT @Size OFFSET @Offset
            """, new { CopyId = copyId, Size = size, Offset = (actualPage - 1) * size }, tx, cancellationToken: ct)))
            .Select(row => new { row.SourceFileId, row.SourceFileName, sourceDeleted = row.SourceStatus != "AVAILABLE",
                row.TargetFileId, row.TargetFileName, targetDeleted = row.TargetStatus != "AVAILABLE" }).ToArray();
        await tx.CommitAsync(ct);
        return new { list, total, page = actualPage, pageSize = size };
    }

    private static async Task<ProjectCopySnapshot> CaptureSnapshotAsync(MySqlConnection conn, CurrentUser actor,
        ulong sourceProjectId, string targetName, CancellationToken ct)
    {
        await using var tx = await AppDb.BeginTransactionAsync(conn, ct);
        var current = await AccessService.LockActorAsync(conn, tx, actor, ct);
        await AccessService.RequirePermissionAsync(conn, tx, current, "project:list", ct);
        await AccessService.RequirePermissionAsync(conn, tx, current, "project:create", ct);
        await ProjectAccessService.RequireViewForValidatedActorAsync(conn, tx, current, sourceProjectId, true, ct);
        var source = await LoadProjectAsync(conn, tx, sourceProjectId, ct);
        var group = await LoadGroupAsync(conn, tx, source.ProjectGroupId, ct);
        if (string.Equals(source.Name, targetName, StringComparison.OrdinalIgnoreCase))
            throw ApiException.BadRequest("复制项目必须使用新的项目名称");
        await EnsureNameUniqueAsync(conn, tx, targetName, ct);
        await ValidateSourceAsync(conn, tx, source, ct);
        await ValidateGroupAsync(conn, tx, group, ct);
        if (group.Status is ProjectStatuses.Completed or ProjectStatuses.Terminated)
            throw ApiException.Conflict("主项目已结束，不能复制子项目");
        if (await ActiveUploadCountAsync(conn, tx, sourceProjectId, ct) != 0)
            throw ApiException.Conflict("源项目仍有进行中的文件上传，请上传完成后再复制");
        var files = await LoadFilesForUpdateAsync(conn, tx, sourceProjectId, ct);
        var copiedAt = await conn.ExecuteScalarAsync<DateTime>(new CommandDefinition(
            "SELECT UTC_TIMESTAMP(6)", transaction: tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return new(source, group, files, copiedAt);
    }

    private static async Task<PreparedCopy> PreparePhysicalCopyAsync(string root, DateTime copiedAt,
        CopyFileRow sourceFile, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sourceFile.Sha256) || sourceFile.Sha256.Length != 64)
            throw ApiException.Conflict($"源文件“{sourceFile.OriginalName}”缺少完整性校验值，无法复制");
        string sourcePath;
        try { sourcePath = await FileStorage.ResolveExistingFileAsync(root, Path.Combine(root, sourceFile.StoragePath), ct); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException or InvalidOperationException)
        { throw ApiException.Conflict($"源文件“{sourceFile.OriginalName}”缺失或存储路径异常，无法复制"); }
        var storedName = $"{Guid.NewGuid():D}.{sourceFile.Ext}";
        var targetPath = FileStorage.FinalPath(root, copiedAt, storedName);
        var directory = Path.GetDirectoryName(targetPath) ?? throw new InvalidOperationException("存储目录无效");
        Directory.CreateDirectory(directory);
        await FileStorage.ResolveExistingAsync(root, directory, false, ct);
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
            return await verification.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS(SELECT 1 FROM project_copies WHERE id=@CopyId AND target_project_id=@TargetProjectId)",
                new { CopyId = copyId, TargetProjectId = targetProjectId }, cancellationToken: timeout.Token));
        }
        catch { return null; }
    }

    private static async Task ValidateSourceAsync(MySqlConnection conn, MySqlTransaction tx, ProjectRow source, CancellationToken ct)
    {
        if (source.WorkOrderNos.Length == 0 || string.IsNullOrWhiteSpace(source.MachineModel)
            || source.RobotVendorId is null or 0 || source.RobotModelId is null or 0
            || source.ResponsibleUserId is null or 0 || source.SectionId is null or 0
            || source.PriorityId is null or 0 || source.ExpectedCompletionDate is null)
            throw ApiException.Conflict("源项目资料不完整，请先补齐必填信息后再复制");
        var valid = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            """
            SELECT EXISTS(SELECT 1 FROM suppliers s
              JOIN users owner ON owner.id=@OwnerId AND owner.user_type='INTERNAL' AND owner.status='ACTIVE'
              JOIN departments section ON section.id=owner.department_id AND section.kind='SECTION'
              JOIN project_dictionaries vendor ON vendor.id=@VendorId AND vendor.type='ROBOT_VENDOR'
              JOIN project_dictionaries model ON model.id=@ModelId AND model.type='ROBOT_MODEL' AND model.parent_id=vendor.id
              JOIN project_dictionaries priority ON priority.id=@PriorityId AND priority.type='PRIORITY'
              WHERE s.id=@SupplierId AND s.status='ACTIVE' AND section.id=@SectionId)
            """, new { OwnerId = source.ResponsibleUserId, VendorId = source.RobotVendorId,
                ModelId = source.RobotModelId, source.PriorityId, source.SupplierId, source.SectionId },
            tx, cancellationToken: ct));
        if (!valid) throw ApiException.Conflict("源项目关联资料已失效，请先更新负责人、课别、供应商或数据字典后再复制");
    }

    private static async Task ValidateGroupAsync(MySqlConnection conn, MySqlTransaction tx, CopyGroupRow group, CancellationToken ct)
    {
        if (group.WorkOrderNos.Length == 0 || string.IsNullOrWhiteSpace(group.MachineModel)
            || group.RobotVendorId is null or 0 || group.RobotModelId is null or 0
            || group.ResponsibleUserId is null or 0 || group.SectionId is null or 0
            || group.PriorityId is null or 0 || group.ExpectedCompletionDate is null)
            throw ApiException.Conflict("主项目资料不完整，请先补齐必填信息后再复制");
        var valid = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            """
            SELECT EXISTS(SELECT 1 FROM suppliers s
              JOIN users owner ON owner.id=@OwnerId AND owner.user_type='INTERNAL' AND owner.status='ACTIVE'
              JOIN departments section ON section.id=owner.department_id AND section.kind='SECTION'
              JOIN project_dictionaries vendor ON vendor.id=@VendorId AND vendor.type='ROBOT_VENDOR'
              JOIN project_dictionaries model ON model.id=@ModelId AND model.type='ROBOT_MODEL' AND model.parent_id=vendor.id
              JOIN project_dictionaries priority ON priority.id=@PriorityId AND priority.type='PRIORITY'
              WHERE s.id=@SupplierId AND s.status='ACTIVE' AND section.id=@SectionId)
            """, new { OwnerId = group.ResponsibleUserId, VendorId = group.RobotVendorId,
                ModelId = group.RobotModelId, group.PriorityId, group.SupplierId, group.SectionId },
            tx, cancellationToken: ct));
        if (!valid) throw ApiException.Conflict("主项目关联资料已失效，请先更新负责人、课别、供应商或数据字典后再复制");
    }

    private static async Task<ProjectRow> LoadProjectAsync(MySqlConnection conn, MySqlTransaction tx, ulong projectId, CancellationToken ct)
    {
        var row = await conn.QuerySingleOrDefaultAsync<ProjectRow>(new CommandDefinition(
            """
            SELECT p.id AS Id,p.project_group_id AS ProjectGroupId,g.name AS ProjectGroupName,
              p.name AS Name,p.description AS Description,p.supplier_id AS SupplierId,
              p.machine_model AS MachineModel,p.robot_vendor_id AS RobotVendorId,rv.name AS RobotVendorName,
              p.robot_model_id AS RobotModelId,rm.name AS RobotModelName,
              p.responsible_user_id AS ResponsibleUserId,owner.employee_no AS ResponsibleUserEmployeeNo,
              owner.real_name AS ResponsibleUserName,p.section_id AS SectionId,section.name AS SectionName,
              p.priority_id AS PriorityId,priority.name AS PriorityName,
              p.expected_completion_date AS ExpectedCompletionDate,p.status AS Status,p.confirm_side AS ConfirmSide,
              p.created_by AS CreatedBy,p.created_at AS CreatedAt,p.updated_at AS UpdatedAt,s.name AS SupplierName,u.real_name AS CreatedByName
            FROM projects p LEFT JOIN project_groups g ON g.id=p.project_group_id
              LEFT JOIN suppliers s ON s.id=p.supplier_id LEFT JOIN users u ON u.id=p.created_by
              LEFT JOIN project_dictionaries rv ON rv.id=p.robot_vendor_id LEFT JOIN project_dictionaries rm ON rm.id=p.robot_model_id
              LEFT JOIN users owner ON owner.id=p.responsible_user_id LEFT JOIN departments section ON section.id=p.section_id AND section.kind='SECTION'
              LEFT JOIN project_dictionaries priority ON priority.id=p.priority_id WHERE p.id=@ProjectId
            """, new { ProjectId = projectId }, tx, cancellationToken: ct));
        if (row is null) throw ApiException.NotFound();
        row.WorkOrderNos = (await conn.QueryAsync<string>(new CommandDefinition(
            "SELECT work_order_no FROM project_work_orders WHERE project_id=@ProjectId ORDER BY sort_no,id",
            new { ProjectId = projectId }, tx, cancellationToken: ct))).ToArray();
        return row;
    }

    private static async Task<CopyGroupRow> LoadGroupAsync(MySqlConnection conn, MySqlTransaction tx, ulong groupId, CancellationToken ct)
    {
        var row = await conn.QuerySingleOrDefaultAsync<CopyGroupRow>(new CommandDefinition(
            """
            SELECT id AS Id,supplier_id AS SupplierId,status AS Status,machine_model AS MachineModel,
              robot_vendor_id AS RobotVendorId,robot_model_id AS RobotModelId,
              responsible_user_id AS ResponsibleUserId,section_id AS SectionId,priority_id AS PriorityId,
              expected_completion_date AS ExpectedCompletionDate,updated_at AS UpdatedAt
            FROM project_groups WHERE id=@GroupId
            """, new { GroupId = groupId }, tx, cancellationToken: ct));
        if (row is null) throw ApiException.NotFound("主项目不存在");
        row.WorkOrderNos = (await conn.QueryAsync<string>(new CommandDefinition(
            "SELECT work_order_no FROM project_group_work_orders WHERE project_group_id=@GroupId ORDER BY sort_no,id",
            new { GroupId = groupId }, tx, cancellationToken: ct))).ToArray();
        return row;
    }

    private static async Task<CopyFileRow[]> LoadFilesForUpdateAsync(MySqlConnection conn, MySqlTransaction tx,
        ulong projectId, CancellationToken ct) => (await conn.QueryAsync<CopyFileRow>(new CommandDefinition(
        "SELECT id AS Id,uploader_id AS UploaderId,direction AS Direction,original_name AS OriginalName,ext AS Ext,size_bytes AS SizeBytes,mime_type AS MimeType,sha256 AS Sha256,storage_path AS StoragePath,status AS Status FROM files WHERE project_id=@ProjectId ORDER BY id FOR UPDATE",
        new { ProjectId = projectId }, tx, cancellationToken: ct))).ToArray();

    private static Task<ulong> ActiveUploadCountAsync(MySqlConnection conn, MySqlTransaction tx, ulong projectId, CancellationToken ct) =>
        conn.ExecuteScalarAsync<ulong>(new CommandDefinition(
            "SELECT COUNT(*) FROM upload_sessions WHERE project_id=@ProjectId AND status IN ('UPLOADING','MERGING')",
            new { ProjectId = projectId }, tx, cancellationToken: ct));

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

    private static async Task EnsureNameUniqueAsync(MySqlConnection conn, MySqlTransaction tx, string name, CancellationToken ct)
    {
        if (await conn.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT EXISTS(SELECT 1 FROM projects WHERE name=@Name)",
                new { Name = name }, tx, cancellationToken: ct))) throw ApiException.Conflict("项目名称已存在");
    }

    private static string ValidateName(string? value)
    {
        var name = (value ?? string.Empty).Trim();
        if (name.Length == 0 || name.EnumerateRunes().Count() > 128) throw ApiException.BadRequest("新项目名称需为 1~128 个字符");
        return name;
    }

    private static async Task InsertActivitiesAsync(MySqlConnection conn, MySqlTransaction tx, ulong sourceProjectId,
        ulong targetProjectId, ulong copyId, ulong actorId, string actorName, int fileCount,
        DateTime createdAt, CancellationToken ct) => await conn.ExecuteAsync(new CommandDefinition(
        """
        INSERT INTO project_activities(project_id,activity_type,action,actor_id,actor_name,occurred_at,title,summary,target_id,source_key)
        VALUES (@SourceProjectId,'PROJECT','COPY',@ActorId,@ActorName,@CreatedAt,'复制项目',@Summary,@SourceProjectId,@SourceKey),
               (@TargetProjectId,'PROJECT','COPY',@ActorId,@ActorName,@CreatedAt,'由复制创建',@Summary,@TargetProjectId,@TargetKey)
        """, new { SourceProjectId = sourceProjectId, TargetProjectId = targetProjectId, ActorId = actorId, ActorName = actorName,
            CreatedAt = createdAt, Summary = $"复制了 {fileCount} 个文件", SourceKey = $"project-copy:{copyId}:source",
            TargetKey = $"project-copy:{copyId}:target" }, tx, cancellationToken: ct));

    private static object Snapshot(ProjectRow p) => new { id = p.Id, p.Name, p.Status, p.SupplierId, p.WorkOrderNos,
        p.MachineModel, p.RobotVendorId, p.RobotModelId, p.ResponsibleUserId, p.SectionId, p.PriorityId, p.ExpectedCompletionDate };
    private static object HistoryItem(CopyHistoryRow row, ulong projectId, string name) => new { row.CopyId, projectId, name,
        row.FileCount, row.TotalBytes, row.CopiedByName, createdAt = ProjectJson.Utc(row.CreatedAt) };

    private static async Task<bool> CanViewAsync(MySqlConnection conn, MySqlTransaction tx, CurrentUser actor,
        ulong projectId, CancellationToken ct)
    {
        try { await ProjectAccessService.RequireViewForValidatedActorAsync(conn, tx, actor, projectId, false, ct); return true; }
        catch (ApiException error) when (error.Status is 403 or 404) { return false; }
    }

    private static Task<ulong> LastInsertIdAsync(MySqlConnection conn, MySqlTransaction tx, CancellationToken ct) =>
        conn.ExecuteScalarAsync<ulong>(new CommandDefinition("SELECT LAST_INSERT_ID()", transaction: tx, cancellationToken: ct));
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
    private sealed record CopyFileRow(ulong Id, ulong UploaderId, string Direction, string OriginalName, string Ext, ulong SizeBytes,
        string? MimeType, string? Sha256, string StoragePath, string Status);
    private sealed class CopyHistoryRow
    {
        public ulong CopyId { get; init; } public ulong SourceProjectId { get; init; } public string SourceName { get; init; } = "";
        public ulong SourceSupplierId { get; init; } public ulong? SourceResponsibleUserId { get; init; }
        public ulong TargetProjectId { get; init; } public string TargetName { get; init; } = "";
        public ulong TargetSupplierId { get; init; } public ulong? TargetResponsibleUserId { get; init; }
        public string CopiedByName { get; init; } = "";
        public ulong FileCount { get; init; } public ulong TotalBytes { get; init; } public DateTime CreatedAt { get; init; }
    }
    private sealed class ProjectCopyIds { public ulong SourceProjectId { get; init; } public ulong TargetProjectId { get; init; } }
    private sealed class FileCopyHistoryRow
    {
        public ulong SourceFileId { get; init; } public string SourceFileName { get; init; } = ""; public ulong TargetFileId { get; init; }
        public string TargetFileName { get; init; } = ""; public string? SourceStatus { get; init; } public string? TargetStatus { get; init; }
    }
}
