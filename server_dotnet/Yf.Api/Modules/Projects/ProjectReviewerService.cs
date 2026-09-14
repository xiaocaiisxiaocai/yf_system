using Dapper;
using MySqlConnector;

namespace Yf.Api.Modules.Projects;

internal static class ProjectReviewerService
{
    internal static async Task<IReadOnlyList<UserRow>> ListAsync(
        MySqlConnection conn,
        MySqlTransaction? tx,
        ProjectRow project,
        CancellationToken ct)
    {
        const string sql = """
            SELECT DISTINCT u.id AS Id,u.employee_no AS EmployeeNo,u.real_name AS RealName,
                   u.email AS Email,u.user_type AS UserType,u.supplier_id AS SupplierId,
                   u.department_id AS DepartmentId,u.status AS Status
            FROM users u
            WHERE u.status='ACTIVE' AND u.user_type='INTERNAL'
              AND EXISTS(
                  SELECT 1
                  FROM user_roles ur
                  INNER JOIN roles r ON r.id=ur.role_id AND r.status='ACTIVE'
                  INNER JOIN role_permissions rp ON rp.role_id=r.id
                  INNER JOIN permissions permission ON permission.id=rp.permission_id
                  WHERE ur.user_id=u.id AND permission.code='project:list'
              )
              AND EXISTS(
                  SELECT 1
                  FROM user_roles ur
                  INNER JOIN roles r ON r.id=ur.role_id AND r.status='ACTIVE'
                  INNER JOIN role_permissions rp ON rp.role_id=r.id
                  INNER JOIN permissions permission ON permission.id=rp.permission_id
                  WHERE ur.user_id=u.id AND permission.code='project:confirm'
              )
              AND (
                  u.id=@CreatedBy
                  OR EXISTS(
                      SELECT 1 FROM project_members pm
                      WHERE pm.project_id=@ProjectId AND pm.user_id=u.id
                  )
                  OR EXISTS(
                      SELECT 1
                      FROM user_roles ur
                      INNER JOIN roles r ON r.id=ur.role_id AND r.status='ACTIVE'
                      INNER JOIN role_permissions rp ON rp.role_id=r.id
                      INNER JOIN permissions permission ON permission.id=rp.permission_id
                      WHERE ur.user_id=u.id AND permission.code='project:view_all'
                  )
              )
            ORDER BY u.id
            """;
        var rows = await conn.QueryAsync<UserRow>(new CommandDefinition(
            sql,
            new { project.CreatedBy, ProjectId = project.Id },
            tx,
            cancellationToken: ct));
        return rows.AsList();
    }
}
