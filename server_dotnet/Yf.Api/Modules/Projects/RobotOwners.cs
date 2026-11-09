using Microsoft.EntityFrameworkCore;
using Yf.Api.Infrastructure;
using Yf.Api.Infrastructure.Entities;

namespace Yf.Api.Modules.Projects;

/// <summary>
/// Robot owners are supplier contacts selected on a project. <c>robot_owner_name</c> keeps the joined names as a
/// snapshot, so completed projects stay frozen and every existing read path keeps working. See 厂商联络人与签名契约.
/// </summary>
internal static class RobotOwners
{
    internal const int MaximumCount = 10;
    internal const string Separator = "、";

    internal static ulong[] Normalize(ulong[]? ids)
    {
        var normalized = (ids ?? []).Where(id => id != 0).Distinct().ToArray();
        if (normalized.Length == 0) throw ApiException.BadRequest("请选择 Robot 负责人");
        if (normalized.Length > MaximumCount) throw ApiException.BadRequest($"Robot 负责人最多选择 {MaximumCount} 位");
        return normalized;
    }

    /// <summary>
    /// Validates the selection against the project's supplier and returns the joined-name snapshot.
    /// Contacts already selected may stay even after they were disabled; newly added ones must be active.
    /// </summary>
    internal static async Task<string> ResolveSnapshotAsync(
        YfDbContext db, ulong supplierId, IReadOnlyList<ulong> ids, IReadOnlyCollection<ulong> alreadySelected,
        CancellationToken ct)
    {
        var idArray = ids.ToArray();
        var contacts = await db.SupplierContacts.AsNoTracking()
            .Where(contact => Enumerable.Contains(idArray, contact.Id))
            .Select(contact => new { contact.Id, contact.SupplierId, contact.Name, contact.Status })
            .ToDictionaryAsync(contact => contact.Id, ct);
        foreach (var id in ids)
        {
            if (!contacts.TryGetValue(id, out var contact) || contact.SupplierId != supplierId)
                throw ApiException.BadRequest("Robot 负责人必须是该供应商的联络人");
            if (contact.Status != AccountStatuses.Active && !alreadySelected.Contains(id))
                throw ApiException.BadRequest($"联络人「{contact.Name}」已停用，不能选为 Robot 负责人");
        }
        return string.Join(Separator, ids.Select(id => contacts[id].Name));
    }

    internal static async Task ReplaceAsync(YfDbContext db, ulong projectId, IReadOnlyList<ulong> ids, CancellationToken ct)
    {
        await db.ProjectRobotOwners.Where(owner => owner.ProjectId == projectId).ExecuteDeleteAsync(ct);
        db.ProjectRobotOwners.AddRange(ids.Select((id, index) =>
            new ProjectRobotOwner { ProjectId = projectId, ContactId = id, SortNo = index }));
        await db.SaveChangesAsync(ct);
    }

    internal static async Task<Dictionary<ulong, ulong[]>> LoadAsync(
        YfDbContext db, IReadOnlyCollection<ulong> projectIds, CancellationToken ct)
    {
        if (projectIds.Count == 0) return [];
        var ids = projectIds.ToArray();
        return (await db.ProjectRobotOwners.AsNoTracking()
                .Where(owner => Enumerable.Contains(ids, owner.ProjectId))
                .OrderBy(owner => owner.SortNo).ThenBy(owner => owner.ContactId)
                .Select(owner => new { owner.ProjectId, owner.ContactId })
                .ToListAsync(ct))
            .GroupBy(owner => owner.ProjectId)
            .ToDictionary(group => group.Key, group => group.Select(owner => owner.ContactId).ToArray());
    }

    /// <summary>
    /// After a contact is renamed, rebuild the snapshot of every unfinished project that selected it. Completed
    /// projects keep the names they were accepted with, and <c>updated_at</c> is left alone so editors are not forced
    /// into a version conflict.
    /// </summary>
    internal static async Task RefreshSnapshotsAsync(YfDbContext db, ulong contactId, CancellationToken ct)
    {
        var projectIds = await db.ProjectRobotOwners.Where(owner => owner.ContactId == contactId)
            .Select(owner => owner.ProjectId)
            .Where(projectId => db.Projects.Any(project => project.Id == projectId && project.Status != ProjectStatuses.Completed))
            .ToArrayAsync(ct);
        foreach (var projectId in projectIds)
            await RebuildSnapshotAsync(db, projectId, ct);
    }

    /// <summary>
    /// Rewrites one project's snapshot from the current contact names. A project without selected contacts keeps
    /// its old name. Also used when a completed project is reopened, so the names skipped while it was completed
    /// are caught up in the same transaction.
    /// </summary>
    internal static async Task RebuildSnapshotAsync(YfDbContext db, ulong projectId, CancellationToken ct)
    {
        var names = await (from owner in db.ProjectRobotOwners
                           join contact in db.SupplierContacts on owner.ContactId equals contact.Id
                           where owner.ProjectId == projectId
                           orderby owner.SortNo, owner.ContactId
                           select contact.Name).ToArrayAsync(ct);
        if (names.Length == 0) return;
        var snapshot = string.Join(Separator, names);
        await db.Projects.Where(project => project.Id == projectId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(project => project.RobotOwnerName, snapshot)
                // Assigning the column explicitly suppresses its ON UPDATE CURRENT_TIMESTAMP.
                .SetProperty(project => project.UpdatedAt, project => project.UpdatedAt), ct);
    }
}
