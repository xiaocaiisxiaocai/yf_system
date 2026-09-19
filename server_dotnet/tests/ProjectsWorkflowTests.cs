using Yf.Api.Infrastructure;
using Yf.Api.Modules.Projects;

namespace Yf.Api.Tests;

public sealed class ProjectsWorkflowTests
{
    [Theory]
    [InlineData(0UL, 0UL, 0UL, 0UL, "DRAFT")]
    [InlineData(2UL, 2UL, 0UL, 0UL, "DRAFT")]
    [InlineData(3UL, 0UL, 3UL, 0UL, "COMPLETED")]
    [InlineData(3UL, 0UL, 2UL, 1UL, "TERMINATED")]
    [InlineData(3UL, 1UL, 1UL, 0UL, "IN_PROGRESS")]
    [InlineData(3UL, 0UL, 1UL, 0UL, "IN_PROGRESS")]
    public void MainProjectStatusIsDerivedFromEverySubproject(
        ulong total, ulong draft, ulong completed, ulong terminated, string expected)
    {
        Assert.Equal(expected, ProjectGroupStatusService.DeriveStatus(total, draft, completed, terminated));
    }

    [Fact]
    public void ReferencedRobotModelCannotMoveToAnotherVendor()
    {
        var error = Assert.Throws<ApiException>(() =>
            ProjectDictionaryService.EnsureReferencedModelParentUnchanged("ROBOT_MODEL", true, 10, 11));

        Assert.Equal(409, error.Status);
        Assert.Equal("机器人型号已被项目引用，不能更换所属厂商", error.Message);
        ProjectDictionaryService.EnsureReferencedModelParentUnchanged("ROBOT_MODEL", false, 10, 11);
        ProjectDictionaryService.EnsureReferencedModelParentUnchanged("ROBOT_MODEL", true, 10, 10);
    }

    [Fact]
    public void WorkOrdersAreTrimmedDeduplicatedAndKeepFirstOrder()
    {
        var result = ProjectService.NormalizeWorkOrderNos([" WO-2 ", "", null, "wo-2", "WO-1"]);

        Assert.Equal(["WO-2", "WO-1"], result);
    }

    [Fact]
    public void WorkOrderLimitsAreEnforced()
    {
        var tooMany = Enumerable.Range(1, 51).Select(index => (string?)$"WO-{index}").ToArray();
        Assert.Equal("工令号不能超过 50 个",
            Assert.Throws<ApiException>(() => ProjectService.NormalizeWorkOrderNos(tooMany)).Message);
        Assert.Equal("单个工令号不能超过 128 个字符",
            Assert.Throws<ApiException>(() => ProjectService.NormalizeWorkOrderNos([new string('号', 129)])).Message);
    }

    [Theory]
    [InlineData("DRAFT", "IN_PROGRESS", "IN_PROGRESS", "START")]
    [InlineData("TERMINATED", "IN_PROGRESS", "IN_PROGRESS", "RESTART")]
    [InlineData("IN_PROGRESS", "TERMINATED", "TERMINATED", "TERMINATE")]
    public void ManagementTransitionsRespectProjectStateMachine(
        string from,
        string requested,
        string expectedTo,
        string expectedAction)
    {
        var transition = ProjectWorkflowRules.ManagementTransition(from, requested);
        Assert.Equal(expectedTo, transition.To);
        Assert.Equal(expectedAction, transition.Action);
    }

    [Theory]
    [InlineData("PENDING_CONFIRMATION", "IN_PROGRESS")]
    [InlineData("COMPLETED", "TERMINATED")]
    public void IllegalManagementTransitionsPreserveTheBusinessError(string from, string requested)
    {
        var error = Assert.Throws<ApiException>(() => ProjectWorkflowRules.ManagementTransition(from, requested));
        Assert.Equal(409, error.Status);
        Assert.Equal(40901, error.Code);
    }

    [Fact]
    public void UnknownManagementTargetIsARequestError()
    {
        var error = Assert.Throws<ApiException>(() => ProjectWorkflowRules.ManagementTransition("DRAFT", "COMPLETED"));
        Assert.Equal(400, error.Status);
        Assert.Equal("仅允许开始、终止或重新开始项目", error.Message);
    }

    [Fact]
    public void ProjectsWithoutDeletionDependenciesCanBeDeleted()
    {
        ProjectWorkflowRules.EnsureNoDeletionDependencies(false, false);
    }

    [Fact]
    public void ContentAndUploadHistoryBlockDeletionWithDistinctErrors()
    {
        var content = Assert.Throws<ApiException>(() => ProjectWorkflowRules.EnsureNoDeletionDependencies(true, false));
        Assert.Equal("项目内仍有文件或留言，不能直接删除", content.Message);

        var upload = Assert.Throws<ApiException>(() => ProjectWorkflowRules.EnsureNoDeletionDependencies(false, true));
        Assert.Equal("项目仍有上传记录，不能删除", upload.Message);
    }

    [Theory]
    [InlineData("DRAFT")]
    [InlineData("TERMINATED")]
    public void DraftAndTerminatedProjectsAreDeletable(string status)
    {
        ProjectWorkflowRules.EnsureDeletable(status);
    }

    [Theory]
    [InlineData("IN_PROGRESS")]
    [InlineData("PENDING_CONFIRMATION")]
    [InlineData("COMPLETED")]
    public void ActiveAndCompletedProjectsCannotBeDeleted(string status)
    {
        var error = Assert.Throws<ApiException>(() => ProjectWorkflowRules.EnsureDeletable(status));
        Assert.Equal(400, error.Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("COMPANY")]
    [InlineData(" COMPANY ")]
    public void SubmissionConfirmationSideDefaultsToCompany(string? requestedSide)
    {
        Assert.Equal("COMPANY", ProjectWorkflowRules.NormalizeConfirmSide(requestedSide));
    }

    [Theory]
    [InlineData("SUPPLIER")]
    [InlineData("")]
    [InlineData("company")]
    public void NonCompanyConfirmationRequestsAreRejectedClearly(string requestedSide)
    {
        var error = Assert.Throws<ApiException>(() => ProjectWorkflowRules.NormalizeConfirmSide(requestedSide));

        Assert.Equal(400, error.Status);
        Assert.Equal("项目验收仅支持公司内部确认，confirmSide 必须为 COMPANY", error.Message);
    }

    [Fact]
    public void OnlyInternalUsersCanConfirmOrRejectAndReceivePendingAcceptance()
    {
        var internalUser = new CurrentUser(1, "internal", "INTERNAL", null);
        var supplierUser = new CurrentUser(2, "supplier", "SUPPLIER", 10);

        ProjectWorkflowRules.RequireInternalDecisionActor(internalUser);
        Assert.True(ProjectWorkflowRules.CanReceivePendingAcceptance(internalUser, hasConfirmPermission: true));
        Assert.False(ProjectWorkflowRules.CanReceivePendingAcceptance(internalUser, hasConfirmPermission: false));
        Assert.False(ProjectWorkflowRules.CanReceivePendingAcceptance(supplierUser, hasConfirmPermission: true));
        var error = Assert.Throws<ApiException>(() => ProjectWorkflowRules.RequireInternalDecisionActor(supplierUser));
        Assert.Equal(403, error.Status);
        Assert.Equal("项目验收确认和驳回仅限公司内部用户", error.Message);
    }

    [Fact]
    public void OnlySupplierUsersCanSubmitForAcceptance()
    {
        var supplierUser = new CurrentUser(2, "supplier", "SUPPLIER", 10);
        var internalUser = new CurrentUser(1, "internal", "INTERNAL", null);

        ProjectWorkflowRules.RequireSupplierSubmitter(supplierUser);
        var error = Assert.Throws<ApiException>(() => ProjectWorkflowRules.RequireSupplierSubmitter(internalUser));

        Assert.Equal(403, error.Status);
        Assert.Equal("项目验收仅允许供应商用户提交", error.Message);
    }

    [Fact]
    public void OnlySupplierUsersCanWithdrawAcceptance()
    {
        var supplierUser = new CurrentUser(2, "supplier", "SUPPLIER", 10);
        var internalUser = new CurrentUser(1, "internal", "INTERNAL", null);

        ProjectWorkflowRules.RequireSupplierWithdrawer(supplierUser);
        var error = Assert.Throws<ApiException>(() => ProjectWorkflowRules.RequireSupplierWithdrawer(internalUser));
        Assert.Equal(403, error.Status);
        Assert.Equal("项目验收仅允许供应商用户撤回", error.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0UL)]
    public void WorkflowDecisionRequiresASubmissionVersion(ulong? expectedSubmissionId)
    {
        var error = Assert.Throws<ApiException>(() =>
            ProjectService.RequireExpectedSubmissionId(expectedSubmissionId));

        Assert.Equal(400, error.Status);
        Assert.Equal("expectedSubmissionId 必须为当前待验收提交版本", error.Message);
    }

    [Fact]
    public void WorkflowDecisionRejectsAStaleSubmissionVersion()
    {
        var latest = new ProjectStatusLogRow { Id = 102, ProjectId = 7, Action = "SUBMIT" };

        var error = Assert.Throws<ApiException>(() =>
            ProjectService.EnsureExpectedSubmission(latest, 101));

        Assert.Equal(409, error.Status);
        Assert.Equal("验收申请已更新，请刷新项目后重新操作", error.Message);
        ProjectService.EnsureExpectedSubmission(latest, 102);
    }
}
