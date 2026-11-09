using Yf.Api.Infrastructure;
using Yf.Api.Modules.Projects;

namespace Yf.Api.Tests;

public sealed class ProjectsWorkflowTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData(" IN_PROGRESS ", "IN_PROGRESS")]
    [InlineData("COMPLETED", "COMPLETED")]
    [InlineData("TERMINATED", "TERMINATED")]
    public void ProjectStatusFilterAcceptsOnlyLiveStatuses(string? value, string? expected)
    {
        Assert.Equal(expected, ProjectService.NormalizeStatus(value));
    }

    [Theory]
    [InlineData("DRAFT")]
    [InlineData("PENDING_CONFIRMATION")]
    [InlineData("UNKNOWN")]
    public void ProjectStatusFilterRejectsRetiredAndUnknownValues(string value)
    {
        var error = Assert.Throws<ApiException>(() => ProjectService.NormalizeStatus(value));
        Assert.Equal(400, error.Status);
    }

    [Theory]
    [InlineData("IN_PROGRESS", "TERMINATED", "TERMINATED", "TERMINATE")]
    [InlineData("TERMINATED", "IN_PROGRESS", "IN_PROGRESS", "RESTART")]
    public void ManagementTransitionsAreTerminateAndRestart(string from, string requested, string to, string action)
    {
        Assert.Equal((to, action), ProjectWorkflowRules.ManagementTransition(from, requested));
    }

    [Theory]
    [InlineData("DRAFT", "IN_PROGRESS", 409)]
    [InlineData("COMPLETED", "TERMINATED", 409)]
    [InlineData("IN_PROGRESS", "IN_PROGRESS", 409)]
    [InlineData("IN_PROGRESS", "COMPLETED", 400)]
    public void OtherManagementTransitionsAreRefused(string from, string requested, int status)
    {
        Assert.Equal(status, Assert.Throws<ApiException>(() => ProjectWorkflowRules.ManagementTransition(from, requested)).Status);
    }

    [Theory]
    [InlineData("TERMINATED", true)]
    [InlineData("IN_PROGRESS", false)]
    [InlineData("COMPLETED", false)]
    [InlineData("DRAFT", false)]
    public void OnlyTerminatedProjectsAreDeletable(string status, bool deletable)
    {
        var error = Record.Exception(() => ProjectWorkflowRules.EnsureDeletable(status));
        Assert.Equal(deletable, error is null);
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
    public void AbortRightsStayWithTheResponsibleUserAndTheSupplierUploaders()
    {
        var owner = new CurrentUser(1, "owner", UserTypes.Internal, null);
        var colleague = new CurrentUser(2, "colleague", UserTypes.Internal, null);
        var peer = new CurrentUser(4, "peer", UserTypes.Supplier, 9);
        var otherSupplier = new CurrentUser(5, "other", UserTypes.Supplier, 8);
        // The responsible user aborts its own session (with file:upload) and any session blocking the project.
        Assert.True(UploadActivity.CanAbort(owner, ProjectStatuses.InProgress, true, 1, UserTypes.Internal, null, true));
        Assert.False(UploadActivity.CanAbort(owner, ProjectStatuses.InProgress, false, 1, UserTypes.Internal, null, true));
        Assert.True(UploadActivity.CanAbort(owner, ProjectStatuses.InProgress, false, 6, UserTypes.Supplier, 9, true));
        Assert.False(UploadActivity.CanAbort(owner, ProjectStatuses.Completed, true, 6, UserTypes.Supplier, 9, true));
        // Other internal accounts cannot, not even on a session they began before the project was handed over.
        Assert.False(UploadActivity.CanAbort(colleague, ProjectStatuses.InProgress, true, 2, UserTypes.Internal, null, false));
        Assert.False(UploadActivity.CanAbort(colleague, ProjectStatuses.InProgress, true, 6, UserTypes.Supplier, 9, false));
        // Supplier accounts of the same company share their sessions.
        Assert.True(UploadActivity.CanAbort(peer, ProjectStatuses.Completed, true, 6, UserTypes.Supplier, 9, false));
        Assert.False(UploadActivity.CanAbort(peer, ProjectStatuses.InProgress, true, 1, UserTypes.Internal, null, false));
        Assert.False(UploadActivity.CanAbort(peer, ProjectStatuses.InProgress, false, 6, UserTypes.Supplier, 9, false));
        Assert.False(UploadActivity.CanAbort(otherSupplier, ProjectStatuses.InProgress, true, 6, UserTypes.Supplier, 9, false));
    }
}
