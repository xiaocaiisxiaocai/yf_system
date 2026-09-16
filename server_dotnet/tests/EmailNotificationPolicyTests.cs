using Yf.Api.Modules.SystemManagement;

namespace Yf.Api.Tests;

public sealed class EmailNotificationPolicyTests
{
    [Fact]
    public void AudienceSwitchesAreIndependent()
    {
        var policy = new EmailNotificationPolicy(
            GlobalEnabled: true,
            InternalEnabled: false,
            SupplierEnabled: true,
            MessageCreatedEnabled: true,
            FileUploadedEnabled: true,
            ProjectSubmittedEnabled: true,
            ProjectConfirmedEnabled: true,
            ProjectRejectedEnabled: true,
            ProjectWithdrawnEnabled: true);

        Assert.False(policy.Allows("MESSAGE_CREATED", "INTERNAL"));
        Assert.True(policy.Allows("MESSAGE_CREATED", "SUPPLIER"));
    }

    [Fact]
    public void EventSwitchesAreIndependentAndUnknownEventsRemainGlobalOnly()
    {
        var policy = new EmailNotificationPolicy(
            GlobalEnabled: true,
            InternalEnabled: true,
            SupplierEnabled: true,
            MessageCreatedEnabled: false,
            FileUploadedEnabled: true,
            ProjectSubmittedEnabled: true,
            ProjectConfirmedEnabled: true,
            ProjectRejectedEnabled: true,
            ProjectWithdrawnEnabled: true);

        Assert.False(policy.Allows("MESSAGE_CREATED", "SUPPLIER"));
        Assert.True(policy.Allows("FILE_UPLOADED", "SUPPLIER"));
        Assert.True(policy.Allows("TEST_NOTIFICATION", null));
    }

    [Fact]
    public void GlobalSwitchOverridesAudienceAndEventRules()
    {
        var policy = new EmailNotificationPolicy(
            GlobalEnabled: false,
            InternalEnabled: true,
            SupplierEnabled: true,
            MessageCreatedEnabled: true,
            FileUploadedEnabled: true,
            ProjectSubmittedEnabled: true,
            ProjectConfirmedEnabled: true,
            ProjectRejectedEnabled: true,
            ProjectWithdrawnEnabled: true);

        Assert.False(policy.Allows("MESSAGE_CREATED", "INTERNAL"));
        Assert.False(policy.Allows("TEST_NOTIFICATION", null));
    }
}
