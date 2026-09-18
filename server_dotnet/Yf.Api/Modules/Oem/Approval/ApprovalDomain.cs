using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.Oem.Approval;

public static class ApproverSources
{
    public const string SectionLeader = "SECTION_LEADER";
    public const string DepartmentLeader = "DEPARTMENT_LEADER";
    public const string DivisionLeader = "DIVISION_LEADER";
    public const string SpecifiedUsers = "SPECIFIED_USERS";

    public static readonly IReadOnlyList<string> All = [SectionLeader, DepartmentLeader, DivisionLeader, SpecifiedUsers];
    public static bool IsLeader(string source) => source is SectionLeader or DepartmentLeader or DivisionLeader;
}

public static class ApprovalModes
{
    public const string Single = "SINGLE";
    public const string Any = "ANY";
    public const string All = "ALL";
}

public static class SelfPolicies
{
    public const string Designated = "DESIGNATED";
    public const string Skip = "SKIP";
    public const string Block = "BLOCK";

    public static readonly IReadOnlyList<string> AllValues = [Designated, Skip, Block];
}

public static class NodeUserRoles
{
    public const string Approver = "APPROVER";
    public const string Fallback = "FALLBACK";
}

public static class FlowInstanceStatuses
{
    public const string WaitingScan = "WAITING_SCAN";
    public const string InProgress = "IN_PROGRESS";
    public const string ApprovalBlocked = "APPROVAL_BLOCKED";
    public const string Completed = "COMPLETED";
    public const string Skipped = "SKIPPED";
    public const string Rejected = "REJECTED";
    public const string Cancelled = "CANCELLED";

    public static bool IsOpen(string status) => status is WaitingScan or InProgress or ApprovalBlocked;
}

public static class FlowNodeStatuses
{
    public const string Waiting = "WAITING";
    public const string Pending = "PENDING";
    public const string Approved = "APPROVED";
    public const string Rejected = "REJECTED";
    public const string Skipped = "SKIPPED";
    public const string Cancelled = "CANCELLED";
}

public static class FlowTaskStatuses
{
    public const string Waiting = "WAITING";
    public const string Pending = "PENDING";
    public const string Approved = "APPROVED";
    public const string Rejected = "REJECTED";
    public const string NotNeeded = "NOT_NEEDED";
    public const string Superseded = "SUPERSEDED";
    public const string Cancelled = "CANCELLED";

    public static bool IsOpen(string status) => status is Waiting or Pending;
}

public static class SkipReasons
{
    public const string SelfPolicySkip = "SELF_POLICY_SKIP";
}

public sealed record FlowNodeDefinition(
    int SortNo, string Name, string Source, string Mode, string SelfPolicy, bool Enabled,
    IReadOnlyList<ulong> Approvers, IReadOnlyList<ulong> Fallbacks);

public sealed record FlowTemplateDefinition(ulong Id, string Name, ulong Version, IReadOnlyList<FlowNodeDefinition> Nodes);

public sealed record OrgLevel(ulong Id, string Name, string Kind, ulong? LeaderUserId);

/// <summary>The initiator's organisation chain at send time: section, and its department and division when present.</summary>
public sealed record OrgPath(OrgLevel Section, OrgLevel? Department, OrgLevel? Division)
{
    /// <summary>Most specific first: the order in which template scopes are matched.</summary>
    public IEnumerable<OrgLevel> MostSpecificFirst()
    {
        yield return Section;
        if (Department is not null) yield return Department;
        if (Division is not null) yield return Division;
    }
}

/// <summary>Whether a user may be assigned an OEM approval task right now (active internal account holding oem:flow_approve).</summary>
public interface IApproverEligibility
{
    bool IsEligible(ulong userId);
}

public sealed class SetEligibility(IEnumerable<ulong> eligible) : IApproverEligibility
{
    private readonly HashSet<ulong> ids = eligible.ToHashSet();
    public bool IsEligible(ulong userId) => ids.Contains(userId);
}

public sealed record PlannedNode(
    int SortNo, string Name, string Source, string Mode, bool Skipped, string? SkipReason, bool UsedFallback,
    IReadOnlyList<ulong> Approvers, string? ScopeName);

public sealed record ApprovalPlan(IReadOnlyList<PlannedNode> Nodes)
{
    /// <summary>True when no enabled node needs a human decision (all skipped by policy, or none enabled).</summary>
    public bool RequiresNoApproval => Nodes.All(node => node.Skipped);
    public IEnumerable<PlannedNode> ActiveNodes => Nodes.Where(node => !node.Skipped).OrderBy(node => node.SortNo);
}

/// <summary>A send cannot be planned; the message is shown to the sender verbatim and the draft is kept.</summary>
public sealed class ApprovalPlanningException(string message) : Exception(message)
{
    public ApiException ToApi() => new(400, 40011, "发送失败：" + Message);
}

/// <summary>Resolves the candidate approvers of one node (strategy per approver source).</summary>
public interface IApproverSourceStrategy
{
    string Source { get; }

    /// <summary>Candidates before self-policy and eligibility are applied, plus the organisation name for messages.</summary>
    (IReadOnlyList<ulong> Candidates, string ScopeName) Candidates(FlowNodeDefinition node, OrgPath path);
}

internal sealed class LeaderSourceStrategy(string source, Func<OrgPath, OrgLevel?> level, string levelLabel) : IApproverSourceStrategy
{
    public string Source => source;

    public (IReadOnlyList<ulong> Candidates, string ScopeName) Candidates(FlowNodeDefinition node, OrgPath path)
    {
        var org = level(path) ?? throw new ApprovalPlanningException($"节点「{node.Name}」需要{levelLabel}主管，但发送人所在组织没有上级{levelLabel}");
        if (org.LeaderUserId is not ulong leader)
            throw new ApprovalPlanningException($"{org.Name}未配置主管，请联系管理员维护组织主管");
        return ([leader], org.Name);
    }
}

internal sealed class SpecifiedUsersStrategy : IApproverSourceStrategy
{
    public string Source => ApproverSources.SpecifiedUsers;

    public (IReadOnlyList<ulong> Candidates, string ScopeName) Candidates(FlowNodeDefinition node, OrgPath path) =>
        (node.Approvers, "指定人员");
}

/// <summary>
/// Pure approval planning: given a template, the initiator's organisation path and an
/// eligibility oracle, produce the complete frozen plan or explain why sending must
/// fail. No I/O happens here, so every routing rule is unit-testable.
/// </summary>
public sealed class ApprovalPlanner
{
    private readonly IReadOnlyDictionary<string, IApproverSourceStrategy> strategies;

    public ApprovalPlanner(IEnumerable<IApproverSourceStrategy> strategies) =>
        this.strategies = strategies.ToDictionary(strategy => strategy.Source, StringComparer.Ordinal);

    public static ApprovalPlanner Default { get; } = new([
        new LeaderSourceStrategy(ApproverSources.SectionLeader, path => path.Section, "课别"),
        new LeaderSourceStrategy(ApproverSources.DepartmentLeader, path => path.Department, "部门"),
        new LeaderSourceStrategy(ApproverSources.DivisionLeader, path => path.Division, "事业部"),
        new SpecifiedUsersStrategy(),
    ]);

    public ApprovalPlan Plan(FlowTemplateDefinition template, OrgPath path, ulong initiatorId, IApproverEligibility eligibility)
    {
        var planned = new List<PlannedNode>();
        foreach (var node in template.Nodes.Where(node => node.Enabled).OrderBy(node => node.SortNo))
        {
            if (!strategies.TryGetValue(node.Source, out var strategy))
                throw new ApprovalPlanningException($"节点「{node.Name}」的审批人来源无效");
            var (candidates, scopeName) = strategy.Candidates(node, path);
            planned.Add(ApprovalPlanner.PlanNode(node, candidates, scopeName, initiatorId, eligibility));
        }

        var seen = new Dictionary<ulong, string>();
        foreach (var node in planned.Where(node => !node.Skipped))
        foreach (var approver in node.Approvers)
        {
            if (seen.TryGetValue(approver, out var other))
                throw new ApprovalPlanningException($"同一审批人同时出现在「{other}」和「{node.Name}」节点，请调整审批模板");
            seen[approver] = node.Name;
        }
        return new ApprovalPlan(planned);
    }

    private static PlannedNode PlanNode(FlowNodeDefinition node, IReadOnlyList<ulong> candidates, string scopeName, ulong initiatorId,
        IApproverEligibility eligibility)
    {
        var others = candidates.Where(id => id != initiatorId).Distinct().ToArray();
        var initiatorIsApprover = others.Length < candidates.Distinct().Count();

        if (ApproverSources.IsLeader(node.Source))
        {
            if (initiatorIsApprover) return ApplySelfPolicy(node, scopeName, initiatorId, eligibility);
            var leader = others.Single();
            if (!eligibility.IsEligible(leader))
                throw new ApprovalPlanningException($"{scopeName}的主管账号已停用或没有 OEM 审批权限");
            return new PlannedNode(node.SortNo, node.Name, node.Source, ApprovalModes.Single, false, null, false, [leader], scopeName);
        }

        // Specified users: the initiator never approves their own transfer.
        if (node.Mode == ApprovalModes.All && others.Any(id => !eligibility.IsEligible(id)))
            throw new ApprovalPlanningException($"节点「{node.Name}」为会签，但其中有审批人已停用或没有 OEM 审批权限");
        var eligible = others.Where(eligibility.IsEligible).ToArray();
        if (eligible.Length > 0)
            return new PlannedNode(node.SortNo, node.Name, node.Source, node.Mode, false, null, false, eligible, scopeName);
        if (initiatorIsApprover && others.Length == 0) return ApplySelfPolicy(node, scopeName, initiatorId, eligibility);
        throw new ApprovalPlanningException($"节点「{node.Name}」没有可用的审批人");
    }

    private static PlannedNode ApplySelfPolicy(FlowNodeDefinition node, string scopeName, ulong initiatorId, IApproverEligibility eligibility)
    {
        switch (node.SelfPolicy)
        {
            case SelfPolicies.Skip:
                return new PlannedNode(node.SortNo, node.Name, node.Source, node.Mode, true, SkipReasons.SelfPolicySkip, false, [], scopeName);
            case SelfPolicies.Designated:
                var fallbacks = node.Fallbacks.Where(id => id != initiatorId).Distinct().Where(eligibility.IsEligible).ToArray();
                if (fallbacks.Length == 0)
                    throw new ApprovalPlanningException($"发送人是节点「{node.Name}」的审批人，且该节点没有可用的备用审批人");
                return new PlannedNode(node.SortNo, node.Name, node.Source, ApprovalModes.Any, false, null, true, fallbacks, scopeName);
            default:
                throw new ApprovalPlanningException($"发送人是节点「{node.Name}」的审批人，审批模板不允许本人发送");
        }
    }
}

/// <summary>
/// Chooses the approval template for an initiator: the most specific organisation
/// (section, then department, then division) bound to an active template wins,
/// otherwise the default template applies.
/// </summary>
public static class TemplateSelection
{
    public static (ulong TemplateId, OrgLevel? MatchedScope) Select(OrgPath path, IReadOnlyDictionary<ulong, ulong> activeScopeTemplates, ulong? defaultTemplateId)
    {
        foreach (var level in path.MostSpecificFirst())
            if (activeScopeTemplates.TryGetValue(level.Id, out var templateId)) return (templateId, level);
        return defaultTemplateId is ulong fallback
            ? (fallback, null)
            : throw new ApprovalPlanningException("没有可用的审批模板，请联系管理员");
    }
}
