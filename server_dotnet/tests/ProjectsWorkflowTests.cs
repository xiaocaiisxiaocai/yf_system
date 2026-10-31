using Yf.Api.Infrastructure;
using Yf.Api.Modules.Projects;

namespace Yf.Api.Tests;

public sealed class ProjectsWorkflowTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData(" DRAFT ", "DRAFT")]
    [InlineData("IN_PROGRESS", "IN_PROGRESS")]
    [InlineData("COMPLETED", "COMPLETED")]
    [InlineData("TERMINATED", "TERMINATED")]
    public void MainProjectStatusFilterAcceptsOnlyDerivedStatuses(string? value, string? expected)
    {
        Assert.Equal(expected, ProjectGroupService.NormalizeGroupStatus(value));
    }

    [Theory]
    [InlineData("PENDING_CONFIRMATION")]
    [InlineData("UNKNOWN")]
    public void MainProjectStatusFilterRejectsUnknownValues(string value)
    {
        var error = Assert.Throws<ApiException>(() => ProjectGroupService.NormalizeGroupStatus(value));
        Assert.Equal(400, error.Status);
    }

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
    public void ProjectDictionaryOnlyAcceptsPriorityAndRobotType()
    {
        Assert.Equal("PRIORITY", ProjectDictionaryTypes.Normalize(" priority "));
        Assert.Equal("ROBOT_TYPE", ProjectDictionaryTypes.Normalize(" robot_type "));

        foreach (var type in new string?[] { null, "ROBOT_VENDOR", "ROBOT_MODEL" })
        {
            var error = Assert.Throws<ApiException>(() => ProjectDictionaryTypes.Normalize(type));
            Assert.Equal(400, error.Status);
            Assert.Equal("type 仅支持 PRIORITY 或 ROBOT_TYPE", error.Message);
        }
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
    [InlineData("IN_PROGRESS", "IN_PROGRESS")]
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
    [InlineData("COMPLETED")]
    public void ActiveAndCompletedProjectsCannotBeDeleted(string status)
    {
        var error = Assert.Throws<ApiException>(() => ProjectWorkflowRules.EnsureDeletable(status));
        Assert.Equal(400, error.Status);
    }

    [Theory]
    [InlineData("IN_PROGRESS", null, null, null, "NO_MATERIAL")]
    [InlineData("IN_PROGRESS", null, null, 5UL, "PENDING")]
    [InlineData("IN_PROGRESS", "RETURNED", 5UL, 5UL, "RETURNED")]
    [InlineData("IN_PROGRESS", "RETURNED", 4UL, 5UL, "PENDING")]
    [InlineData("IN_PROGRESS", "APPROVED", 5UL, 5UL, "PENDING")]
    [InlineData("DRAFT", null, null, 5UL, null)]
    [InlineData("COMPLETED", "RETURNED", 5UL, 5UL, null)]
    public void MaterialReviewIsReturnedOnlyWhileItCoversTheLatestSupplierFile(
        string status, string? stored, ulong? reviewFileId, ulong? latestSupplierFileId, string? expected)
    {
        Assert.Equal(expected, ProjectWorkflowRules.EffectiveMaterialReview(status, stored, reviewFileId, latestSupplierFileId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0UL)]
    public void MaterialReviewRequiresTheSeenSupplierFile(ulong? expectedSupplierFileId)
    {
        var error = Assert.Throws<ApiException>(() =>
            ProjectService.RequireExpectedSupplierFileId(expectedSupplierFileId));

        Assert.Equal(400, error.Status);
        Assert.Equal("expectedSupplierFileId 必须为当前最新的厂商回传文件", error.Message);
    }

    [Fact]
    public void ExpiredUploadingDoesNotBlockAndMergingAlwaysDoes()
    {
        var now = new DateTime(2026, 10, 22, 8, 0, 0, DateTimeKind.Utc);
        Assert.False(UploadActivity.IsBlocking(ProjectUploadSessionStatuses.Uploading, now, now));
        Assert.False(UploadActivity.IsBlocking(ProjectUploadSessionStatuses.Uploading, now.AddMinutes(-1), now));
        Assert.True(UploadActivity.IsBlocking(ProjectUploadSessionStatuses.Uploading, now.AddMinutes(1), now));
        Assert.True(UploadActivity.IsBlocking(ProjectUploadSessionStatuses.Merging, now.AddMinutes(-1), now));
        Assert.False(UploadActivity.IsBlocking("ABORTED", now.AddHours(1), now));
    }

    [Fact]
    public void AbortRightsStayWithTheUploaderAndNamedCrossUserRoles()
    {
        var now = DateTime.UtcNow;
        var owner = new CurrentUser(1, "owner", UserTypes.Internal, null);
        var reader = new CurrentUser(2, "reader", UserTypes.Internal, null);
        var statusUser = new CurrentUser(3, "status", UserTypes.Internal, null);
        var peer = new CurrentUser(4, "peer", UserTypes.Supplier, 9);
        var otherSupplier = new CurrentUser(5, "other", UserTypes.Supplier, 8);
        Assert.True(UploadActivity.CanAbort(owner, ProjectStatuses.Draft, true, false, false, 1, UserTypes.Internal, null));
        Assert.False(UploadActivity.CanAbort(owner, ProjectStatuses.InProgress, false, true, true, 1, UserTypes.Internal, null));
        Assert.False(UploadActivity.CanAbort(reader, ProjectStatuses.InProgress, true, false, false, 1, UserTypes.Internal, null));
        Assert.True(UploadActivity.CanAbort(statusUser, ProjectStatuses.InProgress, false, false, true, 1, UserTypes.Internal, null));
        Assert.True(UploadActivity.CanAbort(statusUser, ProjectStatuses.InProgress, false, true, false, 1, UserTypes.Supplier, 9));
        Assert.False(UploadActivity.CanAbort(statusUser, ProjectStatuses.Completed, false, true, true, 1, UserTypes.Internal, null));
        Assert.True(UploadActivity.CanAbort(peer, ProjectStatuses.Completed, true, false, false, 6, UserTypes.Supplier, 9));
        Assert.False(UploadActivity.CanAbort(peer, ProjectStatuses.InProgress, true, false, false, 1, UserTypes.Internal, null));
        Assert.False(UploadActivity.CanAbort(peer, ProjectStatuses.InProgress, false, false, false, 6, UserTypes.Supplier, 9));
        Assert.False(UploadActivity.CanAbort(otherSupplier, ProjectStatuses.InProgress, true, false, false, 6, UserTypes.Supplier, 9));
        Assert.NotEqual(default, now);
    }
}
