using Microsoft.EntityFrameworkCore;
using Yf.Api.Infrastructure;
using Yf.Api.Infrastructure.Entities;

namespace Yf.Api.Modules.Projects;

/// <summary>
/// Robot owners are supplier contacts selected on a main project. <c>robot_owner_name</c> on the main project
/// and its subprojects keeps the joined names as a snapshot, so completed subprojects stay frozen and every
/// existing read path keeps working. See 厂商联络人与签名契约.
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
    /// Validates the selection against the main project's supplier and returns the joined-name snapshot.
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

    internal static async Task ReplaceAsync(YfDbContext db, ulong groupId, IReadOnlyList<ulong> ids, CancellationToken ct)
    {
        await db.ProjectGroupRobotOwners.Where(owner => owner.ProjectGroupId == groupId).ExecuteDeleteAsync(ct);
        db.ProjectGroupRobotOwners.AddRange(ids.Select((id, index) =>
            new ProjectGroupRobotOwner { ProjectGroupId = groupId, ContactId = id, SortNo = index }));
        await db.SaveChangesAsync(ct);
    }

    internal static async Task<Dictionary<ulong, ulong[]>> LoadAsync(
        YfDbContext db, IReadOnlyCollection<ulong> groupIds, CancellationToken ct)
    {
        if (groupIds.Count == 0) return [];
        var ids = groupIds.ToArray();
        return (await db.ProjectGroupRobotOwners.AsNoTracking()
                .Where(owner => Enumerable.Contains(ids, owner.ProjectGroupId))
                .OrderBy(owner => owner.SortNo).ThenBy(owner => owner.ContactId)
                .Select(owner => new { owner.ProjectGroupId, owner.ContactId })
                .ToListAsync(ct))
            .GroupBy(owner => owner.ProjectGroupId)
            .ToDictionary(group => group.Key, group => group.Select(owner => owner.ContactId).ToArray());
    }

    /// <summary>
    /// After a contact is renamed, rebuild the snapshot of every open main project that selected it and of
    /// their unfinished subprojects. Completed subprojects keep the names they were accepted with, and
    /// <c>updated_at</c> is left alone so editors are not forced into a version conflict.
    /// </summary>
    internal static async Task RefreshSnapshotsAsync(YfDbContext db, ulong contactId, CancellationToken ct)
    {
        var groupIds = await db.ProjectGroupRobotOwners.Where(owner => owner.ContactId == contactId)
            .Select(owner => owner.ProjectGroupId)
            .Where(groupId => db.ProjectGroups.Any(group => group.Id == groupId && group.Status != ProjectStatuses.Completed))
            .ToArrayAsync(ct);
        foreach (var groupId in groupIds)
        {
            var names = await (from owner in db.ProjectGroupRobotOwners
                               join contact in db.SupplierContacts on owner.ContactId equals contact.Id
                               where owner.ProjectGroupId == groupId
                               orderby owner.SortNo, owner.ContactId
                               select contact.Name).ToArrayAsync(ct);
            var snapshot = string.Join(Separator, names);
            await db.ProjectGroups.Where(group => group.Id == groupId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(group => group.RobotOwnerName, snapshot)
                    // Assigning the column explicitly suppresses its ON UPDATE CURRENT_TIMESTAMP.
                    .SetProperty(group => group.UpdatedAt, group => group.UpdatedAt), ct);
            await db.Projects.Where(project => project.ProjectGroupId == groupId && project.Status != ProjectStatuses.Completed)
                .ExecuteUpdateAsync(setters => setters.SetProperty(project => project.RobotOwnerName, snapshot)
                    .SetProperty(project => project.UpdatedAt, project => project.UpdatedAt), ct);
        }
    }
}
