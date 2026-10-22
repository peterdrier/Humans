using AwesomeAssertions;
using Humans.Base.Enums;
using Xunit;

namespace Humans.Base.Tests.Enums;

/// <summary>
/// Enum members stored as strings: a rename leaves the OLD string in the column, so it needs a
/// migration that UPDATEs the stored values. Other sections' rows live in their own test projects.
/// </summary>
public class EnumStringStabilityTests
{
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
