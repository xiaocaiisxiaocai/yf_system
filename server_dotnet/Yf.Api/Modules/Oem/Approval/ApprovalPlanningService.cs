using Microsoft.EntityFrameworkCore;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Oem.Common;
using Yf.Api.Modules.Oem.Data;

namespace Yf.Api.Modules.Oem.Approval;

public sealed record PlanningResult(OrgPath Path, FlowTemplateDefinition Template, OrgLevel? MatchedScope, ApprovalPlan Plan);

/// <summary>
/// Loads everything the pure <see cref="ApprovalPlanner"/> needs from the database
/// (organisation chain, template, scope bindings, approver eligibility) inside the
/// caller's unit of work, then delegates the routing decision to the planner.
/// </summary>
public sealed class ApprovalPlanningService
{
    private const string OrganizationRequired = "只有归属已启用组织的内部员工才能发送公司出站传递单";

    internal async Task<PlanningResult> PlanAsync(OemUnitOfWork uow, ulong initiatorUserId, CancellationToken ct)
    {
        var path = await LoadOrgPathAsync(uow, initiatorUserId, ct);
        var (templateId, matched) = await SelectTemplateAsync(uow, path, ct);
        var template = await LoadTemplateAsync(uow, templateId, ct);
        var candidates = template.Nodes.SelectMany(node => node.Approvers.Concat(node.Fallbacks))
            .Concat(path.MostSpecificFirst().Where(level => level.LeaderUserId.HasValue).Select(level => level.LeaderUserId!.Value));
        var eligibility = await EligibilityAsync(uow, candidates, ct);
        return new PlanningResult(path, template, matched, ApprovalPlanner.Default.Plan(template, path, initiatorUserId, eligibility));
    }

    /// <summary>Throws the user-facing reason when the user cannot initiate outbound transfers at all.</summary>
    internal async Task<OrgPath> LoadOrgPathAsync(OemUnitOfWork uow, ulong userId, CancellationToken ct)
    {
        var user = await uow.Db.Users.AsNoTracking().Where(item => item.Id == userId)
            .Select(item => new { item.DepartmentId, item.Status, item.UserType }).SingleOrDefaultAsync(ct);
        if (user is null || user.Status != OemStatus.Active || user.UserType != "INTERNAL")
            throw new ApprovalPlanningException("发送人账号不可用");
        if (user.DepartmentId is not ulong departmentId) throw new ApprovalPlanningException(OrganizationRequired);
        var levels = new List<OrgLevel>();
        ulong? nextId = departmentId;
        // The hierarchy is at most three levels deep; the guard only protects against a corrupt cycle.
        while (nextId is ulong id && levels.Count < 8 && levels.All(level => level.Id != id))
        {
            var row = await uow.Db.Departments.AsNoTracking().Where(item => item.Id == id && item.Status == OemStatus.Active)
                .Select(item => new { item.Id, item.Name, item.Kind, item.LeaderAccountId, item.ParentId })
                .SingleOrDefaultAsync(ct);
            if (row is null) break;
            levels.Add(new OrgLevel(row.Id, row.Name, row.Kind, row.LeaderAccountId));
            nextId = row.ParentId;
        }
        if (levels.Count == 0) throw new ApprovalPlanningException(OrganizationRequired);
        return new OrgPath(levels);
    }

    internal async Task<bool> IsOrganizationMemberAsync(OemUnitOfWork uow, ulong userId, CancellationToken ct)
    {
        try
        {
            await LoadOrgPathAsync(uow, userId, ct);
            return true;
        }
        catch (ApprovalPlanningException)
        {
            return false;
        }
    }

    internal async Task<(ulong TemplateId, OrgLevel? Matched)> SelectTemplateAsync(OemUnitOfWork uow, OrgPath path, CancellationToken ct)
    {
        var levelIds = path.MostSpecificFirst().Select(level => level.Id).ToArray();
        var scopes = await uow.Db.OemFlowTemplateScopes.AsNoTracking()
            .Where(scope => Enumerable.Contains(levelIds, scope.DepartmentId))
            .Join(uow.Db.OemFlowTemplates.Where(template => template.Status == OemStatus.Active),
                scope => scope.TemplateId, template => template.Id, (scope, template) => new { scope.DepartmentId, template.Id })
            .ToDictionaryAsync(item => item.DepartmentId, item => item.Id, ct);
        var defaultTemplateId = await uow.Db.OemFlowTemplates.AsNoTracking()
            .Where(template => template.IsDefault && template.Status == OemStatus.Active)
            .OrderBy(template => template.Id)
            .Select(template => (ulong?)template.Id).FirstOrDefaultAsync(ct);
        return TemplateSelection.Select(path, scopes, defaultTemplateId);
    }

    internal async Task<FlowTemplateDefinition> LoadTemplateAsync(OemUnitOfWork uow, ulong templateId, CancellationToken ct)
    {
        var template = await uow.Db.OemFlowTemplates.AsNoTracking().SingleAsync(item => item.Id == templateId, ct);
        var nodes = await uow.Db.OemFlowTemplateNodes.AsNoTracking().Where(node => node.TemplateId == templateId)
            .OrderBy(node => node.SortNo).ToArrayAsync(ct);
        var nodeIds = nodes.Select(node => node.Id).ToArray();
        var users = await uow.Db.OemFlowTemplateNodeUsers.AsNoTracking().Where(user => Enumerable.Contains(nodeIds, user.NodeId))
            .OrderBy(user => user.Id).ToArrayAsync(ct);
        return new FlowTemplateDefinition(template.Id, template.Name, template.ConcurrencyVersion, nodes.Select(node => new FlowNodeDefinition(
            node.SortNo, node.Name, node.ApproverSource, node.ApprovalMode, node.SelfPolicy, node.Enabled,
            users.Where(user => user.NodeId == node.Id && user.Role == NodeUserRoles.Approver).Select(user => user.UserId).ToArray(),
            users.Where(user => user.NodeId == node.Id && user.Role == NodeUserRoles.Fallback).Select(user => user.UserId).ToArray()))
            .ToArray());
    }

    /// <summary>Active internal accounts among <paramref name="userIds"/> that currently hold oem:flow_approve through an active role.</summary>
    internal static async Task<IApproverEligibility> EligibilityAsync(OemUnitOfWork uow, IEnumerable<ulong> userIds, CancellationToken ct) =>
        new SetEligibility(await EligibleIdsAsync(uow.Db, userIds, ct));

    internal static async Task<ulong[]> EligibleIdsAsync(YfDbContext db, IEnumerable<ulong> userIds, CancellationToken ct)
    {
        var ids = userIds.Distinct().ToArray();
        if (ids.Length == 0) return [];
        return await db.Users.AsNoTracking()
            .Where(user => Enumerable.Contains(ids, user.Id) && user.Status == OemStatus.Active && user.UserType == "INTERNAL")
            .Where(user => db.UserRoles.Any(userRole => userRole.UserId == user.Id
                && db.Roles.Any(role => role.Id == userRole.RoleId && role.Status == OemStatus.Active)
                && db.RolePermissions.Any(rolePermission => rolePermission.RoleId == userRole.RoleId
                    && db.Permissions.Any(permission => permission.Id == rolePermission.PermissionId && permission.Code == OemPermissions.FlowApprove))))
            .Select(user => user.Id).ToArrayAsync(ct);
    }
}
