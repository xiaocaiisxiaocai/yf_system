using Microsoft.EntityFrameworkCore;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Oem.Common;
using Yf.Api.Modules.Oem.Data;

namespace Yf.Api.Modules.Oem.Approval;

public sealed record FlowNodeInput(
    string Name, string ApproverSource, string? ApprovalMode, string SelfPolicy, bool Enabled,
    ulong[]? ApproverUserIds, ulong[]? FallbackUserIds);

public sealed record FlowTemplateCreate(string Name, bool IsDefault, FlowNodeInput[] Nodes, ulong[]? DepartmentIds);
public sealed record FlowTemplateUpdate(string Name, string Status, bool IsDefault, ulong? Version);
public sealed record FlowTemplateDefinitionUpdate(
    string Name, string Status, bool IsDefault, FlowNodeInput[] Nodes, ulong[]? DepartmentIds, ulong? Version);
public sealed record FlowTemplateNodesUpdate(FlowNodeInput[] Nodes, ulong? Version);
public sealed record FlowTemplateScopesUpdate(ulong[] DepartmentIds, ulong? Version);

/// <summary>
/// Administration of approval templates: metadata, the ordered node list (with
/// specified/fallback approvers) and the organisation scopes a template applies to.
/// Every change bumps the template version; sends snapshot the template, so edits
/// here only affect future transfers.
/// </summary>
public sealed class OemFlowTemplateService(IDbContextFactory<YfDbContext> dbFactory, ApprovalPlanningService planning, OemAuditWriter audit)
{
    public const int MaximumNodes = 10;
    public const int MaximumUsersPerNode = 20;

    public async Task<object> ListAsync(OemActor actor, CancellationToken ct)
    {
        await using var uow = await OemUnitOfWork.ReadAsync(dbFactory, ct);
        await OemAuthorizer.RequireInternalAsync(uow, actor, OemPermissions.FlowTemplateManage, ct);
        var templates = await uow.Db.OemFlowTemplates.AsNoTracking().OrderByDescending(item => item.IsDefault).ThenBy(item => item.Id).ToArrayAsync(ct);
        var result = new List<object>(templates.Length);
        foreach (var template in templates) result.Add(await JsonAsync(uow, template, ct));
        return result;
    }

    public async Task<object> DetailAsync(OemActor actor, ulong id, CancellationToken ct)
    {
        await using var uow = await OemUnitOfWork.ReadAsync(dbFactory, ct);
        await OemAuthorizer.RequireInternalAsync(uow, actor, OemPermissions.FlowTemplateManage, ct);
        var template = await uow.Db.OemFlowTemplates.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, ct) ?? throw ApiException.NotFound();
        return await JsonAsync(uow, template, ct);
    }

    public async Task<object> CreateAsync(OemActor actor, FlowTemplateCreate request, CancellationToken ct)
    {
        var name = OemValidation.RequiredText(request.Name, "模板名称", 64);
        await using var uow = await OemUnitOfWork.BeginAsync(dbFactory, ct);
        var current = await OemAuthorizer.RequireInternalAsync(uow, actor, OemPermissions.FlowTemplateManage, ct);
        var nodes = await ValidateNodesAsync(uow, request.Nodes, ct);
        if (await uow.Db.OemFlowTemplates.AnyAsync(item => item.Name == name, ct)) throw ApiException.Conflict("模板名称已存在");
        var template = new OemFlowTemplate
        {
            Name = name, IsDefault = false, Status = OemStatus.Active, ConcurrencyVersion = 0, CreatedBy = current.User.Id,
            CreatedAt = uow.Now, UpdatedAt = uow.Now,
        };
        uow.Db.OemFlowTemplates.Add(template);
        await uow.Db.SaveChangesAsync(ct);
        await WriteNodesAsync(uow, template.Id, nodes, ct);
        if (request.DepartmentIds is { Length: > 0 }) await WriteScopesAsync(uow, template.Id, request.DepartmentIds, ct);
        if (request.IsDefault) await MakeDefaultAsync(uow, template, ct);
        await audit.WriteAsync(uow, current, "OEM_FLOW_TEMPLATE_CREATE", "oem_flow_template", template.Id,
            new { targetName = template.Name, nodes = nodes.Count, scopes = request.DepartmentIds?.Length ?? 0, template.IsDefault }, ct);
        await uow.CommitAsync(ct);
        return await JsonAsync(uow, template, ct);
    }

    public async Task<object> UpdateAsync(OemActor actor, ulong id, FlowTemplateUpdate request, CancellationToken ct)
    {
        var name = OemValidation.RequiredText(request.Name, "模板名称", 64);
        var status = OemStatus.Normalize(request.Status);
        var version = OemValidation.ExpectedVersion(request.Version);
        await using var uow = await OemUnitOfWork.BeginAsync(dbFactory, ct);
        var current = await OemAuthorizer.RequireInternalAsync(uow, actor, OemPermissions.FlowTemplateManage, ct);
        var template = await LockAsync(uow, id, version, ct);
        if (await uow.Db.OemFlowTemplates.AnyAsync(item => item.Name == name && item.Id != id, ct)) throw ApiException.Conflict("模板名称已存在");
        if (template.IsDefault && !request.IsDefault) throw ApiException.BadRequest("请先将其他模板设为默认模板");
        if (status == OemStatus.Disabled && (template.IsDefault || request.IsDefault)) throw ApiException.BadRequest("默认模板不能停用");
        var changes = AuditChange.OnlyChanged(
            new AuditChange("name", "模板名称", template.Name, name),
            new AuditChange("status", "状态", template.Status, status),
            new AuditChange("isDefault", "默认模板", template.IsDefault, request.IsDefault));
        template.Name = name;
        template.Status = status;
        Touch(uow, template);
        await uow.Db.SaveChangesAsync(ct);
        if (request.IsDefault && !template.IsDefault) await MakeDefaultAsync(uow, template, ct);
        await audit.WriteAsync(uow, current, "OEM_FLOW_TEMPLATE_UPDATE", "oem_flow_template", id, new { targetName = template.Name, changes }, ct);
        await uow.CommitAsync(ct);
        return await JsonAsync(uow, template, ct);
    }

    /// <summary>Replaces metadata, nodes and scopes in one transaction for the web editor.</summary>
    public async Task<object> UpdateDefinitionAsync(OemActor actor, ulong id, FlowTemplateDefinitionUpdate request, CancellationToken ct)
    {
        var name = OemValidation.RequiredText(request.Name, "模板名称", 64);
        var status = OemStatus.Normalize(request.Status);
        var version = OemValidation.ExpectedVersion(request.Version);
        if (request.DepartmentIds is null || request.DepartmentIds.Length > 500) throw ApiException.BadRequest("适用组织数量无效");

        await using var uow = await OemUnitOfWork.BeginAsync(dbFactory, ct);
        var current = await OemAuthorizer.RequireInternalAsync(uow, actor, OemPermissions.FlowTemplateManage, ct);
        var template = await LockAsync(uow, id, version, ct);
        var nodes = await ValidateNodesAsync(uow, request.Nodes, ct);
        if (await uow.Db.OemFlowTemplates.AnyAsync(item => item.Name == name && item.Id != id, ct))
            throw ApiException.Conflict("模板名称已存在");
        if (template.IsDefault && !request.IsDefault) throw ApiException.BadRequest("请先将其他模板设为默认模板");
        if (status == OemStatus.Disabled && (template.IsDefault || request.IsDefault)) throw ApiException.BadRequest("默认模板不能停用");

        var beforeNodes = await planning.LoadTemplateAsync(uow, id, ct);
        var beforeScopes = await uow.Db.OemFlowTemplateScopes.AsNoTracking().Where(scope => scope.TemplateId == id)
            .Select(scope => scope.DepartmentId).OrderBy(value => value).ToArrayAsync(ct);
        var changes = AuditChange.OnlyChanged(
            new AuditChange("name", "模板名称", template.Name, name),
            new AuditChange("status", "状态", template.Status, status),
            new AuditChange("isDefault", "默认模板", template.IsDefault, request.IsDefault),
            new AuditChange("nodes", "审批节点", beforeNodes.Nodes, nodes),
            new AuditChange("departments", "适用组织", beforeScopes, request.DepartmentIds.Distinct().Order().ToArray()));

        await uow.Db.OemFlowTemplateNodes.Where(node => node.TemplateId == id).ExecuteDeleteAsync(ct);
        await WriteNodesAsync(uow, id, nodes, ct);
        await uow.Db.OemFlowTemplateScopes.Where(scope => scope.TemplateId == id).ExecuteDeleteAsync(ct);
        await WriteScopesAsync(uow, id, request.DepartmentIds, ct);
        template.Name = name;
        template.Status = status;
        Touch(uow, template);
        await uow.Db.SaveChangesAsync(ct);
        if (request.IsDefault && !template.IsDefault) await MakeDefaultAsync(uow, template, ct);
        await audit.WriteAsync(uow, current, "OEM_FLOW_TEMPLATE_UPDATE", "oem_flow_template", id,
            new { targetName = template.Name, changes }, ct);
        await uow.CommitAsync(ct);
        return await JsonAsync(uow, template, ct);
    }

    public async Task<object> ReplaceNodesAsync(OemActor actor, ulong id, FlowTemplateNodesUpdate request, CancellationToken ct)
    {
        var version = OemValidation.ExpectedVersion(request.Version);
        await using var uow = await OemUnitOfWork.BeginAsync(dbFactory, ct);
        var current = await OemAuthorizer.RequireInternalAsync(uow, actor, OemPermissions.FlowTemplateManage, ct);
        var template = await LockAsync(uow, id, version, ct);
        var nodes = await ValidateNodesAsync(uow, request.Nodes, ct);
        var before = await planning.LoadTemplateAsync(uow, id, ct);
        await uow.Db.OemFlowTemplateNodes.Where(node => node.TemplateId == id).ExecuteDeleteAsync(ct);
        await WriteNodesAsync(uow, id, nodes, ct);
        Touch(uow, template);
        await uow.Db.SaveChangesAsync(ct);
        await audit.WriteAsync(uow, current, "OEM_FLOW_TEMPLATE_NODES", "oem_flow_template", id, new
        {
            targetName = template.Name,
            changes = AuditChange.OnlyChanged(new AuditChange("nodes", "审批节点", before.Nodes, nodes)),
        }, ct);
        await uow.CommitAsync(ct);
        return await JsonAsync(uow, template, ct);
    }

    public async Task<object> ReplaceScopesAsync(OemActor actor, ulong id, FlowTemplateScopesUpdate request, CancellationToken ct)
    {
        var version = OemValidation.ExpectedVersion(request.Version);
        if (request.DepartmentIds is null || request.DepartmentIds.Length > 500) throw ApiException.BadRequest("适用组织数量无效");
        await using var uow = await OemUnitOfWork.BeginAsync(dbFactory, ct);
        var current = await OemAuthorizer.RequireInternalAsync(uow, actor, OemPermissions.FlowTemplateManage, ct);
        var template = await LockAsync(uow, id, version, ct);
        var before = await uow.Db.OemFlowTemplateScopes.Where(scope => scope.TemplateId == id).Select(scope => scope.DepartmentId).OrderBy(x => x).ToArrayAsync(ct);
        await uow.Db.OemFlowTemplateScopes.Where(scope => scope.TemplateId == id).ExecuteDeleteAsync(ct);
        await WriteScopesAsync(uow, id, request.DepartmentIds, ct);
        Touch(uow, template);
        await uow.Db.SaveChangesAsync(ct);
        await audit.WriteAsync(uow, current, "OEM_FLOW_TEMPLATE_SCOPES", "oem_flow_template", id, new
        {
            targetName = template.Name,
            changes = AuditChange.OnlyChanged(new AuditChange("departments", "适用组织", before, request.DepartmentIds.Distinct().Order().ToArray())),
        }, ct);
        await uow.CommitAsync(ct);
        return await JsonAsync(uow, template, ct);
    }

    /// <summary>Shows how a send by <paramref name="userId"/> would be routed right now, without writing anything.</summary>
    public async Task<object> PreviewAsync(OemActor actor, ulong userId, CancellationToken ct)
    {
        await using var uow = await OemUnitOfWork.ReadAsync(dbFactory, ct);
        await OemAuthorizer.RequireInternalAsync(uow, actor, OemPermissions.FlowTemplateManage, ct);
        try
        {
            var result = await planning.PlanAsync(uow, userId, ct);
            var names = await NamesAsync(uow, result.Plan.Nodes.SelectMany(node => node.Approvers), ct);
            return new
            {
                ok = true,
                template = new { result.Template.Id, result.Template.Name },
                matchedScope = result.MatchedScope is null ? null : new { result.MatchedScope.Id, result.MatchedScope.Name, result.MatchedScope.Kind },
                requiresApproval = !result.Plan.RequiresNoApproval,
                nodes = result.Plan.Nodes.Select(node => new
                {
                    node.SortNo, node.Name, approverSource = node.Source, approvalMode = node.Mode, node.Skipped, node.SkipReason,
                    node.UsedFallback, node.ScopeName, approvers = node.Approvers.Select(approver => names.GetValueOrDefault(approver)),
                }),
            };
        }
        catch (ApprovalPlanningException error)
        {
            return new { ok = false, reason = error.Message };
        }
    }

    /// <summary>Internal staff who may be named as approvers (active, holding oem:flow_approve).</summary>
    public async Task<object> ApproverOptionsAsync(OemActor actor, string? keyword, CancellationToken ct)
    {
        await using var uow = await OemUnitOfWork.ReadAsync(dbFactory, ct);
        await OemAuthorizer.RequireInternalAsync(uow, actor, OemPermissions.FlowTemplateManage, ct);
        var query = uow.Db.Users.AsNoTracking().Where(user => user.Status == OemStatus.Active && user.UserType == "INTERNAL");
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var pattern = "%" + keyword.Trim() + "%";
            query = query.Where(user => EF.Functions.Like(user.RealName, pattern) || EF.Functions.Like(user.EmployeeNo, pattern));
        }
        var ids = await query.OrderBy(user => user.EmployeeNo).Select(user => user.Id).Take(500).ToArrayAsync(ct);
        var eligible = await ApprovalPlanningService.EligibleIdsAsync(uow.Db, ids, ct);
        var names = await NamesAsync(uow, eligible, ct);
        return eligible.Take(100).Select(id => names[id]);
    }

    /// <summary>
    /// Active internal staff for picking organisation leaders or previewing routing, flagged
    /// with whether they may currently approve OEM transfers.
    /// </summary>
    public async Task<object> InternalUserOptionsAsync(OemActor actor, string? keyword, CancellationToken ct)
    {
        await using var uow = await OemUnitOfWork.ReadAsync(dbFactory, ct);
        var current = await OemAuthorizer.RecheckAsync(uow, actor, ct);
        if (!await OemAuthorizer.HasAsync(uow, current, OemPermissions.DepartmentLeaderManage, ct)
            && !await OemAuthorizer.HasAsync(uow, current, OemPermissions.FlowTemplateManage, ct))
            throw ApiException.Forbidden();
        var query = uow.Db.Users.AsNoTracking().Where(user => user.Status == OemStatus.Active && user.UserType == "INTERNAL");
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var pattern = "%" + keyword.Trim() + "%";
            query = query.Where(user => EF.Functions.Like(user.RealName, pattern) || EF.Functions.Like(user.EmployeeNo, pattern));
        }
        var users = await query.OrderBy(user => user.EmployeeNo).Take(50)
            .Select(user => new { user.Id, user.EmployeeNo, user.RealName, user.DepartmentId }).ToArrayAsync(ct);
        var eligible = (await ApprovalPlanningService.EligibleIdsAsync(uow.Db, users.Select(user => user.Id), ct)).ToHashSet();
        var departmentIds = users.Where(user => user.DepartmentId.HasValue).Select(user => user.DepartmentId!.Value).Distinct().ToArray();
        var departments = await uow.Db.Departments.AsNoTracking().Where(item => Enumerable.Contains(departmentIds, item.Id))
            .ToDictionaryAsync(item => item.Id, item => item.Name, ct);
        return users.Select(user => new
        {
            user.Id, user.EmployeeNo, user.RealName, canApprove = eligible.Contains(user.Id),
            departmentName = user.DepartmentId is ulong id ? departments.GetValueOrDefault(id) : null,
        });
    }

    private async Task<IReadOnlyList<FlowNodeInput>> ValidateNodesAsync(OemUnitOfWork uow, FlowNodeInput[]? nodes, CancellationToken ct)
    {
        if (nodes is null || nodes.Length is < 1 or > MaximumNodes) throw ApiException.BadRequest($"审批节点数量需为 1~{MaximumNodes} 个");
        var normalized = new List<FlowNodeInput>(nodes.Length);
        foreach (var node in nodes)
        {
            if (node is null) throw ApiException.BadRequest("审批节点不能为空");
            var name = OemValidation.RequiredText(node.Name, "节点名称", 64);
            if (!ApproverSources.All.Contains(node.ApproverSource, StringComparer.Ordinal)) throw ApiException.BadRequest("审批人来源无效");
            if (!SelfPolicies.AllValues.Contains(node.SelfPolicy, StringComparer.Ordinal)) throw ApiException.BadRequest("发起人即审批人时的处理方式无效");
            var approvers = (node.ApproverUserIds ?? []).Distinct().ToArray();
            var fallbacks = (node.FallbackUserIds ?? []).Distinct().ToArray();
            string mode;
            if (ApproverSources.IsLeader(node.ApproverSource))
            {
                if (node.ApprovalMode is not (null or ApprovalModes.Single)) throw ApiException.BadRequest("主管类节点只支持单人审批");
                if (approvers.Length > 0) throw ApiException.BadRequest("主管类节点不能指定审批人");
                mode = ApprovalModes.Single;
            }
            else
            {
                if (node.ApprovalMode is not (ApprovalModes.Any or ApprovalModes.All)) throw ApiException.BadRequest("指定人员节点需选择或签或会签");
                if (approvers.Length is < 1 or > MaximumUsersPerNode) throw ApiException.BadRequest($"指定人员节点需配置 1~{MaximumUsersPerNode} 名审批人");
                mode = node.ApprovalMode;
            }
            if (node.SelfPolicy == SelfPolicies.Designated)
            {
                if (fallbacks.Length is < 1 or > MaximumUsersPerNode) throw ApiException.BadRequest($"「{name}」需配置 1~{MaximumUsersPerNode} 名备用审批人");
            }
            else if (fallbacks.Length > 0) throw ApiException.BadRequest($"「{name}」未选择由备用审批人审批，不能配置备用审批人");
            normalized.Add(new FlowNodeInput(name, node.ApproverSource, mode, node.SelfPolicy, node.Enabled, approvers, fallbacks));
        }
        var people = normalized.SelfMany();
        if (people.Length > 0)
        {
            var eligible = (await ApprovalPlanningService.EligibleIdsAsync(uow.Db, people, ct)).ToHashSet();
            var invalid = people.Where(id => !eligible.Contains(id)).ToArray();
            if (invalid.Length > 0)
                throw ApiException.BadRequest("以下审批人不是启用的内部账号或没有 OEM 审批权限：" +
                    string.Join("、", (await NamesAsync(uow, invalid, ct)).Values.Select(item => item.RealName)));
        }
        return normalized;
    }

    private static async Task WriteNodesAsync(OemUnitOfWork uow, ulong templateId, IReadOnlyList<FlowNodeInput> nodes, CancellationToken ct)
    {
        var entities = nodes.Select((node, index) => new OemFlowTemplateNode
        {
            TemplateId = templateId, SortNo = index + 1, Name = node.Name, ApproverSource = node.ApproverSource,
            ApprovalMode = node.ApprovalMode!, SelfPolicy = node.SelfPolicy, Enabled = node.Enabled,
        }).ToArray();
        uow.Db.OemFlowTemplateNodes.AddRange(entities);
        await uow.Db.SaveChangesAsync(ct);
        for (var i = 0; i < entities.Length; i++)
        {
            uow.Db.OemFlowTemplateNodeUsers.AddRange(nodes[i].ApproverUserIds!.Select(userId =>
                new OemFlowTemplateNodeUser { NodeId = entities[i].Id, UserId = userId, Role = NodeUserRoles.Approver }));
            uow.Db.OemFlowTemplateNodeUsers.AddRange(nodes[i].FallbackUserIds!.Select(userId =>
                new OemFlowTemplateNodeUser { NodeId = entities[i].Id, UserId = userId, Role = NodeUserRoles.Fallback }));
        }
        await uow.Db.SaveChangesAsync(ct);
    }

    private static async Task WriteScopesAsync(OemUnitOfWork uow, ulong templateId, ulong[] departmentIds, CancellationToken ct)
    {
        var ids = departmentIds.Distinct().ToArray();
        if (ids.Length == 0) return;
        var existing = await uow.Db.Departments.AsNoTracking().Where(item => Enumerable.Contains(ids, item.Id)).Select(item => item.Id).ToArrayAsync(ct);
        if (existing.Length != ids.Length) throw ApiException.BadRequest("适用组织不存在");
        var taken = await uow.Db.OemFlowTemplateScopes.AsNoTracking()
            .Where(scope => Enumerable.Contains(ids, scope.DepartmentId) && scope.TemplateId != templateId)
            .Join(uow.Db.OemFlowTemplates, scope => scope.TemplateId, template => template.Id, (scope, template) => template.Name)
            .Distinct().ToArrayAsync(ct);
        if (taken.Length > 0) throw ApiException.Conflict("部分组织已绑定其他审批模板：" + string.Join("、", taken));
        uow.Db.OemFlowTemplateScopes.AddRange(ids.Select(id => new OemFlowTemplateScope { TemplateId = templateId, DepartmentId = id }));
        await uow.Db.SaveChangesAsync(ct);
    }

    private static async Task MakeDefaultAsync(OemUnitOfWork uow, OemFlowTemplate template, CancellationToken ct)
    {
        if (template.Status != OemStatus.Active) throw ApiException.BadRequest("停用的模板不能设为默认模板");
        // Lock every current default so two concurrent promotions serialise.
        var defaults = await uow.Db.OemFlowTemplates.FromSqlRaw("SELECT * FROM oem_flow_templates WHERE is_default=1 FOR UPDATE").ToArrayAsync(ct);
        foreach (var previous in defaults.Where(item => item.Id != template.Id))
        {
            previous.IsDefault = false;
            Touch(uow, previous);
        }
        template.IsDefault = true;
        await uow.Db.SaveChangesAsync(ct);
    }

    private static async Task<OemFlowTemplate> LockAsync(OemUnitOfWork uow, ulong id, ulong expectedVersion, CancellationToken ct)
    {
        var template = await uow.Db.OemFlowTemplates.FromSqlInterpolated($"SELECT * FROM oem_flow_templates WHERE id = {id} FOR UPDATE")
            .SingleOrDefaultAsync(ct) ?? throw ApiException.NotFound();
        OemValidation.MatchVersion(template.ConcurrencyVersion, expectedVersion);
        return template;
    }

    private static void Touch(OemUnitOfWork uow, OemFlowTemplate template)
    {
        template.ConcurrencyVersion++;
        template.UpdatedAt = uow.Now;
    }

    private async Task<object> JsonAsync(OemUnitOfWork uow, OemFlowTemplate template, CancellationToken ct)
    {
        var definition = await planning.LoadTemplateAsync(uow, template.Id, ct);
        var people = definition.Nodes.SelectMany(node => node.Approvers.Concat(node.Fallbacks)).ToArray();
        var names = await NamesAsync(uow, people, ct);
        var eligible = (await ApprovalPlanningService.EligibleIdsAsync(uow.Db, people, ct)).ToHashSet();
        var scopes = await uow.Db.OemFlowTemplateScopes.AsNoTracking().Where(scope => scope.TemplateId == template.Id)
            .Join(uow.Db.Departments, scope => scope.DepartmentId, department => department.Id,
                (scope, department) => new { department.Id, department.Name, department.Kind, department.Status })
            .OrderBy(item => item.Id).ToArrayAsync(ct);
        object Person(ulong id) => new
        {
            id, employeeNo = names.GetValueOrDefault(id)?.EmployeeNo, realName = names.GetValueOrDefault(id)?.RealName, eligible = eligible.Contains(id),
        };
        return new
        {
            template.Id, template.Name, template.IsDefault, template.Status, version = template.ConcurrencyVersion,
            template.CreatedAt, template.UpdatedAt,
            nodes = definition.Nodes.Select(node => new
            {
                node.SortNo, node.Name, approverSource = node.Source, approvalMode = node.Mode, selfPolicy = node.SelfPolicy, node.Enabled,
                approvers = node.Approvers.Select(Person), fallbacks = node.Fallbacks.Select(Person),
            }),
            scopes,
        };
    }

    private static async Task<Dictionary<ulong, PersonName>> NamesAsync(OemUnitOfWork uow, IEnumerable<ulong> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToArray();
        if (list.Length == 0) return [];
        return await uow.Db.Users.AsNoTracking().Where(user => Enumerable.Contains(list, user.Id))
            .Select(user => new PersonName(user.Id, user.EmployeeNo, user.RealName)).ToDictionaryAsync(user => user.Id, ct);
    }

    private sealed record PersonName(ulong Id, string EmployeeNo, string RealName);
}

internal static class FlowNodeInputExtensions
{
    /// <summary>Every person referenced by the node list (approvers and fallbacks).</summary>
    public static ulong[] SelfMany(this IEnumerable<FlowNodeInput> nodes) =>
        nodes.SelectMany(node => (node.ApproverUserIds ?? []).Concat(node.FallbackUserIds ?? [])).Distinct().ToArray();
}
