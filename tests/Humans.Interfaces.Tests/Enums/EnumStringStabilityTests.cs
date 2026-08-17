using AwesomeAssertions;
using Humans.Domain.Enums;
using Xunit;

namespace Humans.Interfaces.Tests.Enums;

/// <summary>
/// Guards against renaming enum members that are stored as strings in the database.
/// If an enum member is renamed, the DB still has the OLD string — causing silent data mismatches.
/// When a rename IS intentional, update the expected names here AND create a DB migration
/// to UPDATE the old values.
/// </summary>
/// <remarks>
/// Only the two enums that <c>Humans.Interfaces</c> still owns are left here. Every other row
/// moved to the test project of the section that owns the enum, one file per section
/// (nobodies-collective/Humans#866): Teams, Shifts, Users, GoogleIntegration and AuditLog, beside
/// the Budget/Campaigns/Feedback/Governance/Issues/Notifications/Tickets halves that had already
/// split out. Neither of these two has a home yet — there is no <c>Humans.Interfaces.Tests</c>,
/// which is what still blocks retiring this project.
/// </remarks>
public class EnumStringStabilityTests
{
    /// <summary>
    /// Verifies that enum member names exactly match what the database stores.
    /// Renames without a corresponding DB migration will silently break queries.
    /// </summary>
    [HumansTheory]
    [MemberData(nameof(StringStoredEnumData))]
    public void StringStoredEnum_MemberNames_MustMatchExpected(
        Type enumType, string[] expectedNames)
    {
        var actualNames = Enum.GetNames(enumType);

        // Existing members must not be renamed
        foreach (var expected in expectedNames)
        {
            actualNames.Should().Contain(expected,
                $"enum {enumType.Name} member '{expected}' is stored as a string in the DB. " +
                $"If you renamed it, create a DB migration to UPDATE the old values.");
        }

        // New members are allowed (append-only), but removed members are not
        // This catches both renames (old name missing) and deletions
    }

    public static TheoryData<Type, string[]> StringStoredEnumData => new()
    {
        {
            typeof(SystemTeamType), ["None", "Volunteers", "Coordinators", "Board", "Asociados", "Colaboradors"]
        },
        {
            // Guarded centrally rather than per section: three sections persist this one
            // enum as a string — campaign_grants.LatestEmailStatus
            // (CampaignGrantConfiguration), survey_invitations.LatestEmailStatus
            // (SurveyInvitationConfiguration), and the Email outbox's own Status
            // (EmailOutboxMessageConfiguration). A rename strands the old string in all three.
            typeof(EmailOutboxStatus), ["Queued", "Sent", "Failed"]
        }
    };
}
