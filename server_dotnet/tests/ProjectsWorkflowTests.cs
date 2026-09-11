using Yf.Api.Infrastructure;
using Yf.Api.Modules.Projects;

namespace Yf.Api.Tests;

public sealed class ProjectsWorkflowTests
{
    [Theory]
    [InlineData("DRAFT", "IN_PROGRESS", "IN_PROGRESS", "START")]
    [InlineData("TERMINATED", "IN_PROGRESS", "IN_PROGRESS", "RESTART")]
    [InlineData("IN_PROGRESS", "TERMINATED", "TERMINATED", "TERMINATE")]
    public void ManagementTransitionsMatchTheRustWorkflow(
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

    [Theory]
    [InlineData("IN_PROGRESS")]
    [InlineData("PENDING_CONFIRMATION")]
    [InlineData("COMPLETED")]
    public void ActiveOrCompletedProjectsCannotBeDeleted(string status)
    {
        var error = Assert.Throws<ApiException>(() => ProjectWorkflowRules.EnsureDeletable(status, false, false));
        Assert.Equal(400, error.Status);
        Assert.Equal("进行中、待确认或已完成的项目不能删除", error.Message);
    }

    [Fact]
    public void EmptyDraftAndTerminatedProjectsCanBeDeleted()
    {
        ProjectWorkflowRules.EnsureDeletable("DRAFT", false, false);
        ProjectWorkflowRules.EnsureDeletable("TERMINATED", false, false);
    }

    [Fact]
    public void ContentAndUploadHistoryBlockDeletionWithDistinctErrors()
    {
        var content = Assert.Throws<ApiException>(() => ProjectWorkflowRules.EnsureDeletable("DRAFT", true, false));
        Assert.Equal("项目内仍有文件或留言，不能直接删除", content.Message);

        var upload = Assert.Throws<ApiException>(() => ProjectWorkflowRules.EnsureDeletable("DRAFT", false, true));
        Assert.Equal("项目仍有上传记录，不能删除", upload.Message);
    }
}
