using Yf.Api.Modules.SystemManagement;

namespace Yf.Api.Tests;

public sealed class EmailNotificationPolicyTests
{
    private static EmailNotificationPolicy AllEnabled() => new(
        GlobalEnabled: true,
        InternalEnabled: true,
        SupplierEnabled: true,
        MessageCreatedEnabled: true,
        FileUploadedEnabled: true,
        ProjectStartedEnabled: true,
        ProjectTerminatedEnabled: true,
        ProjectRestartedEnabled: true,
        ProjectReopenedEnabled: true,
        ProjectMaterialReviewedEnabled: true,
        ProjectDueSoonEnabled: true,
        ProjectOverdueEnabled: true,
        DueInternalEnabled: true,
        DueSupplierEnabled: true,
        DueRemindDaysBefore: 3);

    [Fact]
    public void AudienceSwitchesAreIndependent()
    {
        var policy = AllEnabled() with { InternalEnabled = false };

        Assert.False(policy.Allows("MESSAGE_CREATED", "INTERNAL"));
        Assert.True(policy.Allows("MESSAGE_CREATED", "SUPPLIER"));
    }

    [Fact]
    public void EventSwitchesAreIndependentAndUnknownEventsRemainGlobalOnly()
    {
        var policy = AllEnabled() with { MessageCreatedEnabled = false };

        Assert.False(policy.Allows("MESSAGE_CREATED", "SUPPLIER"));
        Assert.True(policy.Allows("FILE_UPLOADED", "SUPPLIER"));
        Assert.True(policy.Allows("TEST_NOTIFICATION", null));
    }

    [Fact]
    public void MaterialReviewSwitchCoversApprovedAndReturnedMail()
    {
        var policy = AllEnabled() with { ProjectMaterialReviewedEnabled = false };

        Assert.False(policy.Allows("PROJECT_MATERIAL_APPROVED", "SUPPLIER"));
        Assert.False(policy.Allows("PROJECT_MATERIAL_RETURNED", "SUPPLIER"));
        Assert.True(policy.Allows("PROJECT_STARTED", "SUPPLIER"));
    }

    [Theory]
    [InlineData("PROJECT_STARTED")]
    [InlineData("PROJECT_TERMINATED")]
    [InlineData("PROJECT_RESTARTED")]
    [InlineData("PROJECT_REOPENED")]
    public void EachStatusChangeEventHasItsOwnSwitch(string eventType)
    {
        var policy = AllEnabled() with
        {
            ProjectStartedEnabled = eventType != "PROJECT_STARTED",
            ProjectTerminatedEnabled = eventType != "PROJECT_TERMINATED",
            ProjectRestartedEnabled = eventType != "PROJECT_RESTARTED",
            ProjectReopenedEnabled = eventType != "PROJECT_REOPENED",
        };

        foreach (var other in new[] { "PROJECT_STARTED", "PROJECT_TERMINATED", "PROJECT_RESTARTED", "PROJECT_REOPENED" })
        {
            Assert.Equal(other != eventType, policy.Allows(other, "SUPPLIER"));
        }
    }

    [Fact]
    public void DueRemindersHonourTheirOwnAudienceSwitches()
    {
        var supplierOnly = AllEnabled() with { DueInternalEnabled = false };
        Assert.False(supplierOnly.Allows(EmailNotificationPolicy.ProjectDueSoonEvent, "INTERNAL"));
        Assert.True(supplierOnly.Allows(EmailNotificationPolicy.ProjectDueSoonEvent, "SUPPLIER"));
        Assert.True(supplierOnly.Allows("MESSAGE_CREATED", "INTERNAL"));

        var neither = AllEnabled() with { DueInternalEnabled = false, DueSupplierEnabled = false };
        Assert.False(neither.Allows(EmailNotificationPolicy.ProjectOverdueEvent, null));

        var overdueOff = AllEnabled() with { ProjectOverdueEnabled = false };
        Assert.False(overdueOff.Allows(EmailNotificationPolicy.ProjectOverdueEvent, "INTERNAL"));
        Assert.True(overdueOff.Allows(EmailNotificationPolicy.ProjectDueSoonEvent, "INTERNAL"));

        var supplierAudienceOff = AllEnabled() with { SupplierEnabled = false };
        Assert.False(supplierAudienceOff.Allows(EmailNotificationPolicy.ProjectDueSoonEvent, "SUPPLIER"));
    }

    [Fact]
    public void GlobalSwitchOverridesAudienceAndEventRules()
    {
        var policy = AllEnabled() with { GlobalEnabled = false };

        Assert.False(policy.Allows("MESSAGE_CREATED", "INTERNAL"));
        Assert.False(policy.Allows("TEST_NOTIFICATION", null));
    }
}
