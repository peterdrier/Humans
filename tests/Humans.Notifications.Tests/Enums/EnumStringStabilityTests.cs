using AwesomeAssertions;
using Humans.Notifications.Contracts;
using Xunit;

namespace Humans.Notifications.Tests.Enums;

/// <summary>
/// Notifications' string-stored-enum guard. Lives here rather than the central
/// <c>Humans.Domain.Tests.Enums.EnumStringStabilityTests</c> because <see cref="NotificationSource"/>,
/// <see cref="NotificationClass"/> and <see cref="NotificationPriority"/> all sit on
/// Notifications' contracts leaf after the section's G5 move (nobodies-collective/Humans#866),
/// where the central project cannot name them (nobodies-collective/Humans#1025).
/// </summary>
/// <remarks>
/// All three are persisted with <c>HasConversion&lt;string&gt;()</c>: renaming a member leaves
/// the OLD string in <c>notifications.source</c> / <c>.class</c> / <c>.priority</c>. A rename
/// needs a migration that UPDATEs the stored values.
/// <see cref="NotificationSource"/> is doubly load-bearing: it is named by three moved
/// sections plus eleven Base services through the contracts leaf, so a rename is far more
/// likely to be silently missed somewhere than for a single-caller enum.
/// </remarks>
public class EnumStringStabilityTests
{
    [HumansTheory]
    [MemberData(nameof(StringStoredEnumData))]
    public void StringStoredEnum_MemberNames_MustMatchExpected(
        Type enumType, string[] expectedNames)
    {
        var actualNames = Enum.GetNames(enumType);

        foreach (var expected in expectedNames)
        {
            actualNames.Should().Contain(expected,
                $"enum {enumType.Name} member '{expected}' is stored as a string in the DB. " +
                $"If you renamed it, create a DB migration to UPDATE the old values.");
        }
    }

    public static TheoryData<Type, string[]> StringStoredEnumData => new()
    {
        {
            typeof(NotificationSource), [
                "TeamMemberAdded", "ShiftCoverageGap", "ShiftSignupChange", "ConsentReviewNeeded",
                "ApplicationSubmitted", "SyncError", "TermRenewalReminder", "ApplicationApproved",
                "ApplicationRejected", "VolunteerApproved", "ProfileRejected", "AccessSuspended",
                "ReConsentRequired", "TeamJoinRequestSubmitted", "TeamJoinRequestDecided",
                "FeedbackResponse", "WorkspaceCredentialsReady", "RoleAssignmentChanged",
                "CampaignReceived", "TeamMemberRemoved", "ShiftAssigned", "GoogleDriftDetected",
                "FacilitatedMessageReceived", "LegalDocumentPublished", "CampMembershipApproved",
                "CampMembershipRejected", "CampMembershipSeasonClosed", "CampRoleAssigned",
                "IssueComment", "IssueStatusChanged", "IssueAssigned", "IssueSubmitted"
            ]
        },
        {
            typeof(NotificationClass), ["Informational", "Actionable"]
        },
        {
            typeof(NotificationPriority), ["Normal", "High", "Critical"]
        }
    };
}
