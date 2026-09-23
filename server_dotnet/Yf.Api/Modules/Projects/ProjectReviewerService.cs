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
        var rows = await db.Users
            .Where(user => user.Status == AccountStatuses.Active && user.UserType == UserTypes.Internal)
            .Where(user => db.UserRoles.Any(userRole =>
                userRole.UserId == user.Id
                && db.Roles.Any(role => role.Id == userRole.RoleId && role.Status == AccountStatuses.Active)
                && db.RolePermissions.Any(rolePermission =>
                    rolePermission.RoleId == userRole.RoleId
                    && db.Permissions.Any(permission =>
                        permission.Id == rolePermission.PermissionId && permission.Code == "project:list"))))
            .Where(user => db.UserRoles.Any(userRole =>
                userRole.UserId == user.Id
                && db.Roles.Any(role => role.Id == userRole.RoleId && role.Status == AccountStatuses.Active)
                && db.RolePermissions.Any(rolePermission =>
                    rolePermission.RoleId == userRole.RoleId
                    && db.Permissions.Any(permission =>
                        permission.Id == rolePermission.PermissionId && permission.Code == "project:confirm"))))
            .Where(user => user.Id == project.ResponsibleUserId
                || db.UserRoles.Any(userRole =>
                    userRole.UserId == user.Id
                    && db.Roles.Any(role => role.Id == userRole.RoleId && role.Status == AccountStatuses.Active)
                    && db.RolePermissions.Any(rolePermission =>
                        rolePermission.RoleId == userRole.RoleId
                        && db.Permissions.Any(permission =>
                            permission.Id == rolePermission.PermissionId && permission.Code == "project:view_all"))))
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
