using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.Projects;

internal sealed record ProjectDeletionScope(bool HasExplicitPermission, IReadOnlySet<ulong> SupervisedSectionIds)
{
    internal bool CanDelete(ulong? sectionId) =>
        HasExplicitPermission || sectionId is ulong id && SupervisedSectionIds.Contains(id);
}

/// <summary>
/// Project deletion defaults to administrators and the leaders of the project's section or one of its
/// parent organizations. An explicit project:delete grant remains available for deliberate RBAC overrides.
/// </summary>
internal static class ProjectDeletionAuthorization
{
    internal static IQueryable<ulong> SectionsLedBy(YfDbContext db, ulong userId)
    {
        var departments = db.Departments;
        return departments.Where(section => section.Kind == "SECTION"
                && (section.LeaderAccountId == userId
                    || departments.Any(department => department.Id == section.ParentId
                        && department.Kind == "DEPARTMENT"
                        && (department.LeaderAccountId == userId
                            || departments.Any(division => division.Id == department.ParentId
                                && division.Kind == "DIVISION" && division.LeaderAccountId == userId)))))
            .Select(section => section.Id);
    }

    internal static async Task<ProjectDeletionScope> LoadScopeAsync(
        MySqlConnection conn, MySqlTransaction? tx, CurrentUser current, CancellationToken ct)
    {
        if (!current.IsInternal) return new(false, new HashSet<ulong>());
        if (await AccessService.HasPermissionAsync(conn, tx, current.Id, "project:delete", ct))
            return new(true, new HashSet<ulong>());
        await using var db = EfDb.Use(conn, tx);
        var sections = await SectionsLedBy(db, current.Id).ToHashSetAsync(ct);
        return new(false, sections);
    }

    internal static async Task RequireAsync(
        MySqlConnection conn, MySqlTransaction tx, CurrentUser current, ulong? sectionId, CancellationToken ct)
    {
        var scope = await LoadScopeAsync(conn, tx, current, ct);
        if (!scope.CanDelete(sectionId)) throw ApiException.Forbidden();
    }
}
