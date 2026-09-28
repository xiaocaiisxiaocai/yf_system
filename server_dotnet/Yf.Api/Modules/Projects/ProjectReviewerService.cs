using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.Projects;

internal static class ProjectReviewerService
{
    internal static async Task<IReadOnlyList<UserRow>> ListAsync(
        MySqlConnection conn,
        MySqlTransaction? tx,
        ProjectRow project,
        CancellationToken ct)
    {
        await using var db = EfDb.Use(conn, tx);
        // Same effective-grant definition (active roles only) as every other permission check.
        var listers = AccessService.UsersWithPermission(db, "project:list");
        var confirmers = AccessService.UsersWithPermission(db, "project:confirm");
        var viewAll = AccessService.UsersWithPermission(db, "project:view_all");
        var rows = await db.Users
            .Where(user => user.Status == AccountStatuses.Active && user.UserType == UserTypes.Internal)
            .Where(user => listers.Contains(user.Id) && confirmers.Contains(user.Id))
            .Where(user => user.Id == project.ResponsibleUserId
                || db.ProjectGroups.Any(group => group.Id == project.ProjectGroupId && group.CreatedBy == user.Id)
                || viewAll.Contains(user.Id))
            .OrderBy(user => user.Id)
            .Select(user => new UserRow
            {
                Id = user.Id,
                EmployeeNo = user.EmployeeNo,
                RealName = user.RealName,
                Email = user.Email,
                UserType = user.UserType,
                SupplierId = user.SupplierId,
                DepartmentId = user.DepartmentId,
                Status = user.Status,
            })
            .ToArrayAsync(ct);
        return rows;
    }
}
