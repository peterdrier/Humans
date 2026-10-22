using AwesomeAssertions;
using Humans.Users.Contracts;
using Xunit;

namespace Humans.Users.Tests.Enums;

/// <summary>
/// All five are persisted with <c>HasConversion&lt;string&gt;()</c>: renaming a member leaves
/// the OLD string in the column. A rename needs a migration that UPDATEs the stored values.
/// </summary>
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
            typeof(LegacyMembershipTier), ["Volunteer", "Colaborador", "Asociado"]
        },
        {
            typeof(LegacyConsentCheckStatus), ["Pending", "Cleared", "Flagged"]
        },
        {
            typeof(ContactFieldType), ["Email", "Phone", "Signal", "Telegram", "WhatsApp", "Discord", "Other"]
        },
        {
            typeof(ContactFieldVisibility), ["BoardOnly", "CoordinatorsAndBoard", "MyTeams", "AllActiveProfiles"]
        },
        {
            typeof(LegacyMessageCategory), [
                "System", "EventOperations", "CommunityUpdates", "Marketing", "Governance",
                "CampaignCodes", "FacilitatedMessages", "Ticketing", "VolunteerUpdates", "TeamUpdates"
            ]
        }
    };
}
