using AwesomeAssertions;
using Humans.Notifications.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Humans.Web.Tests.Infrastructure;

/// <summary>
/// A source's name is stored on every notification row, so renaming a declared name strands the rows that carry
/// the old one. Every name below has been stored; a section that drops one needs a data migration first.
/// </summary>
public sealed class NotificationSourceNameStabilityTests
{
    private static readonly string[] StoredNames =
    [
        "TeamMemberAdded", "ShiftCoverageGap", "ShiftSignupChange", "ConsentReviewNeeded", "ApplicationSubmitted",
        "SyncError", "TermRenewalReminder", "ApplicationApproved", "ApplicationRejected", "ProfileRejected",
        "AccessSuspended", "ReConsentRequired", "TeamJoinRequestSubmitted", "TeamJoinRequestDecided",
        "FeedbackResponse", "WorkspaceCredentialsReady", "RoleAssignmentChanged", "CampaignReceived",
        "TeamMemberRemoved", "ShiftAssigned", "GoogleDriftDetected", "FacilitatedMessageReceived",
        "LegalDocumentPublished", "CampMembershipApproved", "CampMembershipRejected", "CampMembershipSeasonClosed",
        "CampRoleAssigned", "IssueComment", "IssueStatusChanged", "IssueAssigned", "IssueSubmitted",
        "RideshareInterestReceived", "RideshareInterestAccepted", "RideshareInterestDeclined", "AssemblyVoteOpened",
        "WorkgroupRegistrationPending", "WorkgroupRegistrationDecided", "WorkgroupReportingDue",
        "WorkgroupDocumentActivity", "WorkgroupDispositionRecorded", "WorkgroupMembershipRequested",
        "WorkgroupMembershipDecided", "PreapprovalFileBy", "PreapprovalApplicationReceived",
        "PreapprovalGrantDecided", "GrantReceived", "GrantRevoked",
    ];

    [HumansFact]
    public async Task Every_stored_source_name_is_still_declared()
    {
        await using var factory = new NoDatabaseFactory();

        var declared = factory.Services.GetServices<INotificationSourceContributor>()
            .SelectMany(c => c.Sources)
            .Select(s => s.Name)
            .ToList();

        declared.Should().Contain(StoredNames);
    }
}
