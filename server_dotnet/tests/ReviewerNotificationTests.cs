using Yf.Api.Modules.Projects;

namespace Yf.Api.Tests;

public sealed class ReviewerNotificationTests
{
    [Fact]
    public void AcceptanceDedupeKeyRoundTripsExactRequestAndRecipient()
    {
        var key = ProjectNotificationService.AcceptanceDedupeKey(41, 73, 109);

        Assert.Equal("project-acceptance:41:73:109", key);
        Assert.True(ProjectNotificationService.TryParseAcceptanceDedupeKey(
            key,
            out var projectId,
            out var submissionId,
            out var recipientId));
        Assert.Equal(41UL, projectId);
        Assert.Equal(73UL, submissionId);
        Assert.Equal(109UL, recipientId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("project-acceptance:41:73")]
    [InlineData("project-acceptance:41:73:109:extra")]
    [InlineData("project-acceptance:41:not-a-number:109")]
    [InlineData("other:41:73:109")]
    public void InvalidOrLegacyAcceptanceDedupeKeyIsRejected(string? key)
    {
        Assert.False(ProjectNotificationService.TryParseAcceptanceDedupeKey(
            key,
            out _,
            out _,
            out _));
    }
}
