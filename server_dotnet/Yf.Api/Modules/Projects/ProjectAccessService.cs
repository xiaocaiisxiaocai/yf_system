using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Yf.Api.Infrastructure;
using ProjectEntity = Yf.Api.Infrastructure.Entities.Project;

namespace Yf.Api.Modules.Projects;

public sealed record ProjectAccess(
    ulong Id,
    ulong SupplierId,
    ulong CreatedBy,
    string Status,
    ulong? ResponsibleUserId = null,
    ulong? SectionId = null);

/// <summary>
/// Every internal account with <c>project:list</c> sees every project; supplier accounts see their company's
/// projects. Internal writes additionally require the project's responsible user (<see cref="RequireOwner"/>),
/// checked against the locked row inside the write transaction.
/// </summary>
public static class ProjectAccessService
{
    internal const string OwnerOnlyMessage = "仅项目负责人可以操作该项目";

    internal static async Task<IQueryable<ProjectEntity>> VisibleQueryAsync(
        YfDbContext db,
        CurrentUser current,
        CancellationToken ct)
    {
        if (!await HasPermissionAsync(db, current.Id, "project:list", ct))
            throw ApiException.Forbidden();
        if (current.IsInternal) return db.Projects;
        if (current.SupplierId is null) throw ApiException.OutOfScope();
        var supplierId = current.SupplierId.Value;
        return db.Projects.Where(project => project.SupplierId == supplierId);
    }

    /// <summary>
    /// Projects whose collaboration feed, unread counts, dashboard tasks and mail belong to the caller: an internal
    /// account follows only the projects it is responsible for (it can still open every other one), a supplier
    /// account follows its company's projects.
    /// </summary>
    internal static async Task<IQueryable<ProjectEntity>> InboxQueryAsync(
        YfDbContext db,
        CurrentUser current,
        CancellationToken ct)
    {
        var visible = await VisibleQueryAsync(db, current, ct);
        if (!current.IsInternal) return visible;
        var userId = current.Id;
        return visible.Where(project => project.ResponsibleUserId == userId);
    }

    /// <summary>Internal callers may change a project only while they are its responsible user.</summary>
    internal static void RequireOwner(CurrentUser current, ProjectAccess access)
    {
        if (current.IsInternal && access.ResponsibleUserId != current.Id)
            throw ApiException.Forbidden(OwnerOnlyMessage);
    }

    internal static void RequireOwner(CurrentUser current, ulong? responsibleUserId)
    {
        if (current.IsInternal && responsibleUserId != current.Id)
            throw ApiException.Forbidden(OwnerOnlyMessage);
    }

    public static async Task<ProjectAccess> RequireViewAsync(
        MySqlConnection conn, MySqlTransaction? tx, CurrentUser actor, ulong projectId,
        CancellationToken ct = default)
    {
        if (tx is not null) return await RequireViewCoreAsync(conn, tx, actor, projectId, false, ct);
        await using var owned = await AppDb.BeginTransactionAsync(conn, ct);
        var project = await RequireViewCoreAsync(conn, owned, actor, projectId, false, ct);
        await owned.CommitAsync(ct);
        return project;
    }

    /// <param name="lockRows">
    /// False for chunk writes: they are ordered against merge and abort by the upload-session row lock, and terminate
    /// refuses while a session is active, so locking the project per chunk only serialises uploads.
    /// </param>
    public static async Task<ProjectAccess> RequireFileUploadAsync(
        MySqlConnection conn, MySqlTransaction? tx, CurrentUser actor, ulong projectId,
        CancellationToken ct = default, bool lockRows = true)
    {
        if (tx is null)
        {
            await using var owned = await AppDb.BeginTransactionAsync(conn, ct);
            var result = await RequireFileUploadAsync(conn, owned, actor, projectId, ct, lockRows);
            await owned.CommitAsync(ct);
            return result;
        }
        var project = await RequireViewCoreAsync(conn, tx, actor, projectId, lockRows, ct, ownerOnly: true);
        await AccessService.RequirePermissionAsync(conn, tx, actor, "file:upload", ct);
        if (project.Status != ProjectStatuses.InProgress) throw ApiException.Conflict("项目当前不可上传文件");
        return project;
    }

    public static async Task<ProjectAccess> RequireFileDeleteAsync(
        MySqlConnection conn, MySqlTransaction? tx, CurrentUser actor, ulong projectId,
        CancellationToken ct = default)
    {
        AccessService.RequireInternal(actor);
        if (tx is null)
        {
            await using var owned = await AppDb.BeginTransactionAsync(conn, ct);
            var result = await RequireFileDeleteAsync(conn, owned, actor, projectId, ct);
            await owned.CommitAsync(ct);
            return result;
        }
        var project = await RequireViewCoreAsync(conn, tx, actor, projectId, true, ct, ownerOnly: true);
        await AccessService.RequirePermissionAsync(conn, tx, actor, "file:delete", ct);
        if (project.Status != ProjectStatuses.InProgress) throw ApiException.Conflict("项目当前不可删除文件");
        return project;
    }

    public static async Task<bool> CanDeleteFilesAsync(
        MySqlConnection conn, MySqlTransaction? tx, CurrentUser actor, ProjectAccess project,
        CancellationToken ct = default)
    {
        if (tx is null)
        {
            await using var owned = await AppDb.BeginTransactionAsync(conn, ct);
            var result = await CanDeleteFilesAsync(conn, owned, actor, project, ct);
            await owned.CommitAsync(ct);
            return result;
        }
        var current = await AccessService.ReadActorAsync(conn, tx, actor, ct);
        await using var db = EfDb.Use(conn, tx);
        return current.IsInternal
            && project.Status == ProjectStatuses.InProgress
            && project.ResponsibleUserId == current.Id
            && await HasPermissionAsync(db, current.Id, "file:delete", ct);
    }

    // Both overloads share AccessService's per-transaction permission cache.
    internal static Task<bool> HasPermissionAsync(
        YfDbContext db, ulong userId, string permission, CancellationToken ct) =>
        AccessService.HasPermissionAsync(db.Database.Connection(), db.Database.Transaction(), userId, permission, ct);

    internal static Task<bool> HasPermissionAsync(
        MySqlConnection conn, MySqlTransaction? tx, ulong userId, string permission, CancellationToken ct) =>
        AccessService.HasPermissionAsync(conn, tx, userId, permission, ct);

    internal static async Task<ProjectAccess> RequireViewCoreAsync(
        MySqlConnection conn, MySqlTransaction? tx, CurrentUser actor, ulong projectId, bool forUpdate,
        CancellationToken ct, bool ownerOnly = false)
    {
        if (tx is null) throw new InvalidOperationException("Project access validation requires a transaction.");
        var current = forUpdate
            ? await AccessService.LockActorAsync(conn, tx, actor, ct)
            : await AccessService.ReadActorAsync(conn, tx, actor, ct);
        return await RequireViewForValidatedActorAsync(conn, tx, current, projectId, forUpdate, ct, ownerOnly);
    }

    /// <param name="ownerOnly">
    /// Also require the internal caller to be the responsible user, on the unlocked read and again on the locked row,
    /// so a colleague who can see the project never queues behind, or holds, its lock for a write it may not make.
    /// </param>
    internal static async Task<ProjectAccess> RequireViewForValidatedActorAsync(
        MySqlConnection conn, MySqlTransaction? tx, CurrentUser current, ulong projectId, bool forUpdate,
        CancellationToken ct, bool ownerOnly = false)
    {
        await AccessService.RequirePermissionAsync(conn, tx, current, "project:list", ct);
        await using var db = EfDb.Use(conn, tx);
        var row = await db.Projects.Where(item => item.Id == projectId).Select(item => new ProjectAccess(
            item.Id, item.SupplierId, item.CreatedBy, item.Status,
            item.ResponsibleUserId, item.SectionId)).SingleOrDefaultAsync(ct);
        if (row is null) throw ApiException.NotFound();
        // Scope is checked on an unlocked read first: a caller outside the project's scope must not be
        // able to queue behind, or hold, the row lock of a project it cannot see.
        var visible = Authorize(current, row);
        if (ownerOnly) RequireOwner(current, visible);
        if (!forUpdate) return visible;

        var locked = await db.Database.SqlQuery<LockedProjectAccess>($"""
            SELECT id AS Id,supplier_id AS SupplierId,
                   created_by AS CreatedBy,status AS Status,
                   responsible_user_id AS ResponsibleUserId,section_id AS SectionId
            FROM projects WHERE id={projectId} FOR UPDATE
            """).SingleOrDefaultAsync(ct);
        if (locked is null) throw ApiException.NotFound();
        // Ownership can change between the unlocked read and the lock, so authorise the locked values again.
        var lockedAccess = Authorize(current, new ProjectAccess(
            locked.Id, locked.SupplierId, locked.CreatedBy,
            locked.Status, locked.ResponsibleUserId, locked.SectionId));
        if (ownerOnly) RequireOwner(current, lockedAccess);
        return lockedAccess;
    }

    private static ProjectAccess Authorize(CurrentUser current, ProjectAccess access)
    {
        if (current.IsInternal) return access;
        if (current.SupplierId is null || current.SupplierId.Value != access.SupplierId)
            throw ApiException.OutOfScope();
        return access;
    }

    private sealed class LockedProjectAccess
    {
        public ulong Id { get; init; }
        public ulong SupplierId { get; init; }
        public ulong CreatedBy { get; init; }
        public string Status { get; init; } = "";
        public ulong? ResponsibleUserId { get; init; }
        public ulong? SectionId { get; init; }
    }
}

internal static class ProjectStatuses
{
    internal const string InProgress = "IN_PROGRESS";
    internal const string Completed = "COMPLETED";
    internal const string Terminated = "TERMINATED";
}

internal static class ProjectUploadSessionStatuses
{
    internal const string Uploading = "UPLOADING";
    internal const string Merging = "MERGING";
}

/// <summary>
/// Which upload sessions still block material review, terminate, and copy, and who may abort one.
/// Expired <c>UPLOADING</c> rows do not block; <c>MERGING</c> always does.
/// </summary>
internal static class UploadActivity
{
    internal static bool IsBlocking(string status, DateTime expiresAt, DateTime dbNow) =>
        status == ProjectUploadSessionStatuses.Merging
        || (status == ProjectUploadSessionStatuses.Uploading && expiresAt > dbNow);

    internal static bool CanAbort(
        CurrentUser actor,
        string projectStatus,
        bool canUpload,
        ulong uploaderId,
        string uploaderUserType,
        ulong? uploaderSupplierId,
        bool isResponsibleUser)
    {
        // Internal accounts act on a project only while they are its responsible user, including on their own
        // sessions after a transfer. The responsible user can also clear an abandoned supplier session that would
        // otherwise block the material review until it expires.
        if (actor.IsInternal)
            return isResponsibleUser && projectStatus == ProjectStatuses.InProgress
                && (actor.Id != uploaderId || canUpload);
        if (actor.Id == uploaderId) return canUpload;
        return actor.UserType == UserTypes.Supplier
            && actor.SupplierId is ulong supplierId
            && uploaderUserType == UserTypes.Supplier
            && uploaderSupplierId == supplierId
            && canUpload;
    }
}
