using Yf.Api.Infrastructure;
using Yf.Api.Modules.Oem.Approval;
using Yf.Api.Modules.Oem.Policies;
using Yf.Api.Modules.Oem.Transfers;

namespace Yf.Api.Tests.Oem;

/// <summary>Pure domain rules: approval routing, template selection, retention timing and the transfer state machine.</summary>
public sealed class OemDomainTests
{
    private const ulong Initiator = 10;
    private const ulong SectionLeader = 20;
    private const ulong DepartmentLeader = 30;
    private const ulong DivisionLeader = 40;
    private const ulong Fallback = 50;

    private static OrgPath Path(ulong? sectionLeader = SectionLeader, ulong? departmentLeader = DepartmentLeader, bool withDepartment = true) => new(
        new OrgLevel(1, "装配一课", "SECTION", sectionLeader),
        withDepartment ? new OrgLevel(2, "装配部", "DEPARTMENT", departmentLeader) : null,
        withDepartment ? new OrgLevel(3, "制造事业部", "DIVISION", DivisionLeader) : null);

    private static FlowNodeDefinition Leader(int sort, string source, string selfPolicy = SelfPolicies.Skip, bool enabled = true, params ulong[] fallbacks) =>
        new(sort, "节点" + sort, source, ApprovalModes.Single, selfPolicy, enabled, [], fallbacks);

    private static FlowNodeDefinition Specified(int sort, string mode, ulong[] approvers, string selfPolicy = SelfPolicies.Block, params ulong[] fallbacks) =>
        new(sort, "指定" + sort, ApproverSources.SpecifiedUsers, mode, selfPolicy, true, approvers, fallbacks);

    private static FlowTemplateDefinition Template(params FlowNodeDefinition[] nodes) => new(1, "模板", 0, nodes);

    private static readonly IApproverEligibility Everyone = new SetEligibility([SectionLeader, DepartmentLeader, DivisionLeader, Fallback, 60, 61, Initiator]);

    [Fact]
    public void DefaultTemplateRoutesToTheSectionLeaderAndSkipsDisabledNodes()
    {
        var plan = ApprovalPlanner.Default.Plan(
            Template(Leader(1, ApproverSources.SectionLeader), Leader(2, ApproverSources.DepartmentLeader, enabled: false)),
            Path(), Initiator, Everyone);
        var node = Assert.Single(plan.Nodes);
        Assert.Equal([SectionLeader], node.Approvers);
        Assert.False(plan.RequiresNoApproval);
    }

    [Fact]
    public void LeaderInitiatorFollowsTheNodeSelfPolicy()
    {
        var skip = ApprovalPlanner.Default.Plan(Template(Leader(1, ApproverSources.SectionLeader, SelfPolicies.Skip)), Path(Initiator), Initiator, Everyone);
        Assert.True(skip.RequiresNoApproval);
        Assert.Equal(SkipReasons.SelfPolicySkip, skip.Nodes[0].SkipReason);

        var designated = ApprovalPlanner.Default.Plan(
            Template(Leader(1, ApproverSources.SectionLeader, SelfPolicies.Designated, true, Fallback, Initiator)), Path(Initiator), Initiator, Everyone);
        Assert.True(designated.Nodes[0].UsedFallback);
        Assert.Equal(ApprovalModes.Any, designated.Nodes[0].Mode);
        Assert.Equal([Fallback], designated.Nodes[0].Approvers);

        var blocked = Assert.Throws<ApprovalPlanningException>(() =>
            ApprovalPlanner.Default.Plan(Template(Leader(1, ApproverSources.SectionLeader, SelfPolicies.Block)), Path(Initiator), Initiator, Everyone));
        Assert.Contains("不允许本人发送", blocked.Message);

        // A skipped leader node falls through to the next enabled node.
        var chained = ApprovalPlanner.Default.Plan(
            Template(Leader(1, ApproverSources.SectionLeader), Leader(2, ApproverSources.DepartmentLeader)), Path(Initiator), Initiator, Everyone);
        Assert.True(chained.Nodes[0].Skipped);
        Assert.Equal([DepartmentLeader], chained.Nodes[1].Approvers);
    }

    [Fact]
    public void MissingOrUnavailableLeadersFailWithTheOrganisationName()
    {
        var missing = Assert.Throws<ApprovalPlanningException>(() =>
            ApprovalPlanner.Default.Plan(Template(Leader(1, ApproverSources.SectionLeader)), Path(sectionLeader: null), Initiator, Everyone));
        Assert.Contains("装配一课未配置主管", missing.Message);

        var ineligible = Assert.Throws<ApprovalPlanningException>(() =>
            ApprovalPlanner.Default.Plan(Template(Leader(1, ApproverSources.SectionLeader)), Path(), Initiator, new SetEligibility([])));
        Assert.Contains("没有 OEM 审批权限", ineligible.Message);

        var noDepartment = Assert.Throws<ApprovalPlanningException>(() =>
            ApprovalPlanner.Default.Plan(Template(Leader(1, ApproverSources.DepartmentLeader)), Path(withDepartment: false), Initiator, Everyone));
        Assert.Contains("部门", noDepartment.Message);
    }

    [Fact]
    public void SpecifiedUsersSupportAnyAndAllAndNeverIncludeTheInitiator()
    {
        var any = ApprovalPlanner.Default.Plan(Template(Specified(1, ApprovalModes.Any, [60, 99, Initiator])), Path(), Initiator, Everyone);
        Assert.Equal([60UL], any.Nodes[0].Approvers);

        Assert.Throws<ApprovalPlanningException>(() =>
            ApprovalPlanner.Default.Plan(Template(Specified(1, ApprovalModes.All, [60, 99])), Path(), Initiator, Everyone));
        var all = ApprovalPlanner.Default.Plan(Template(Specified(1, ApprovalModes.All, [60, 61])), Path(), Initiator, Everyone);
        Assert.Equal([60UL, 61UL], all.Nodes[0].Approvers);

        // The only named approver is the initiator: the node's self policy decides.
        var onlySelf = ApprovalPlanner.Default.Plan(Template(Specified(1, ApprovalModes.Any, [Initiator], SelfPolicies.Skip)), Path(), Initiator, Everyone);
        Assert.True(onlySelf.RequiresNoApproval);
        Assert.Throws<ApprovalPlanningException>(() =>
            ApprovalPlanner.Default.Plan(Template(Specified(1, ApprovalModes.Any, [99])), Path(), Initiator, Everyone));
    }

    [Fact]
    public void TheSamePersonInTwoNodesIsAConfigurationError()
    {
        var error = Assert.Throws<ApprovalPlanningException>(() => ApprovalPlanner.Default.Plan(
            Template(Leader(1, ApproverSources.SectionLeader), Specified(2, ApprovalModes.Any, [SectionLeader])), Path(), Initiator, Everyone));
        Assert.Contains("同一审批人", error.Message);
    }

    [Fact]
    public void NoEnabledNodesMeansNoApproval() =>
        Assert.True(ApprovalPlanner.Default.Plan(Template(Leader(1, ApproverSources.SectionLeader, enabled: false)), Path(), Initiator, Everyone).RequiresNoApproval);

    [Fact]
    public void TemplateSelectionPrefersTheMostSpecificOrganisation()
    {
        var scopes = new Dictionary<ulong, ulong> { [2] = 200, [3] = 300 };
        Assert.Equal(200UL, TemplateSelection.Select(Path(), scopes, 1).TemplateId);
        Assert.Equal(100UL, TemplateSelection.Select(Path(), new Dictionary<ulong, ulong> { [1] = 100, [2] = 200 }, 1).TemplateId);
        var fallback = TemplateSelection.Select(Path(), new Dictionary<ulong, ulong>(), 1);
        Assert.Equal(1UL, fallback.TemplateId);
        Assert.Null(fallback.MatchedScope);
        Assert.Throws<ApprovalPlanningException>(() => TemplateSelection.Select(Path(), new Dictionary<ulong, ulong>(), null));
    }

    [Fact]
    public void RetentionStrategiesComputeAndNeverExtendDeadlines()
    {
        var released = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
        var receipt = released.AddHours(2);

        var keep = new RetentionSnapshot(RetentionModes.Keep, null, null);
        Assert.Null(keep.Strategy.InitialPurgeDue(keep, released));
        Assert.Null(keep.Strategy.PurgeDueAfterFirstReceipt(keep, null, null, receipt));

        var afterRelease = new RetentionSnapshot(RetentionModes.AfterRelease, 60 * 24, null);
        Assert.Equal(released.AddDays(1), afterRelease.Strategy.InitialPurgeDue(afterRelease, released));
        Assert.Equal(released.AddDays(1), afterRelease.Strategy.PurgeDueAfterFirstReceipt(afterRelease, released.AddDays(1), released.AddDays(1), receipt));

        var afterReceipt = new RetentionSnapshot(RetentionModes.AfterFirstReceipt, null, 30);
        Assert.Null(afterReceipt.Strategy.InitialPurgeDue(afterReceipt, released));
        Assert.Equal(receipt.AddMinutes(30), afterReceipt.Strategy.PurgeDueAfterFirstReceipt(afterReceipt, null, null, receipt));
        // A later receipt cannot push an existing deadline out.
        Assert.Equal(receipt.AddMinutes(30), afterReceipt.Strategy.PurgeDueAfterFirstReceipt(afterReceipt, receipt.AddMinutes(30), null, receipt.AddHours(5)));

        var combined = new RetentionSnapshot(RetentionModes.FirstReceiptOrDeadline, 180, 600);
        var expires = combined.Strategy.ExpiresAt(combined, released);
        Assert.Equal(released.AddHours(3), expires);
        // The absolute deadline shortens the grace period.
        Assert.Equal(released.AddHours(3), combined.Strategy.PurgeDueAfterFirstReceipt(combined, expires, expires, receipt));
    }

    [Fact]
    public void RetentionValidationRejectsUnusedOrOutOfRangeDurations()
    {
        RetentionStrategies.For(RetentionModes.Keep).Validate(null, null);
        Assert.Throws<ApiException>(() => RetentionStrategies.For(RetentionModes.Keep).Validate(10, null));
        Assert.Throws<ApiException>(() => RetentionStrategies.For(RetentionModes.AfterRelease).Validate(null, null));
        Assert.Throws<ApiException>(() => RetentionStrategies.For(RetentionModes.AfterRelease).Validate(0, null));
        Assert.Throws<ApiException>(() => RetentionStrategies.For(RetentionModes.AfterFirstReceipt).Validate(5, 5));
        Assert.Throws<ApiException>(() => RetentionStrategies.For(RetentionModes.FirstReceiptOrDeadline).Validate(RetentionStrategies.MaximumMinutes + 1, 5));
        Assert.Throws<ApiException>(() => RetentionStrategies.For("NEVER"));
    }

    [Fact]
    public void TransferStateMachineOnlyAllowsDocumentedTransitions()
    {
        Assert.True(TransferStateMachine.CanTransition(TransferLifecycle.Draft, TransferLifecycle.Sealed));
        Assert.True(TransferStateMachine.CanTransition(TransferLifecycle.Sealed, TransferLifecycle.Released));
        Assert.False(TransferStateMachine.CanTransition(TransferLifecycle.Draft, TransferLifecycle.Released));
        Assert.False(TransferStateMachine.CanTransition(TransferLifecycle.Released, TransferLifecycle.Cancelled));
        Assert.False(TransferStateMachine.CanTransition(TransferLifecycle.Rejected, TransferLifecycle.Sealed));
        Assert.Throws<ApiException>(() => TransferStateMachine.Ensure(TransferLifecycle.Blocked, TransferLifecycle.Released));
    }

    [Fact]
    public void SettingDefinitionsNormaliseAndBoundValues()
    {
        var size = OemSettingCatalog.Get(OemSettingCatalog.MaxFileSize);
        Assert.Equal("1048576", size.Normalize(" 1048576 "));
        Assert.Throws<ApiException>(() => size.Normalize("10"));
        var exts = OemSettingCatalog.Get(OemSettingCatalog.AllowedExtsOemToInternal);
        Assert.Equal("pdf,step,zip", exts.Normalize(".STEP, pdf,zip,pdf"));
        Assert.Throws<ApiException>(() => exts.Normalize("p/f"));
        Assert.Equal("false", OemSettingCatalog.Get(OemSettingCatalog.NotifyReceipt).Normalize("0"));
        var settings = new OemSettings(new Dictionary<string, string?> { [OemSettingCatalog.MaxFileSize] = "garbage" });
        Assert.Equal(2147483648UL, settings.MaxFileSize);
    }
}
