using AwesomeAssertions;
using Humans.Campaigns.Domain;

namespace Humans.Campaigns.Tests.Enums;

/// <summary>
/// <see cref="CampaignStatus"/> is persisted with <c>HasConversion&lt;string&gt;()</c>: renaming a member
/// leaves the OLD string in <c>campaigns.status</c>, so a rename needs a migration that UPDATEs it.
/// </summary>
public class EnumStringStabilityTests
{
    [HumansFact]
    public void StringStoredCampaignsEnums_MemberNames_MustMatchExpected()
    {
        AssertNames(typeof(CampaignStatus), ["Draft", "Active", "Completed"]);
    }

    private static void AssertNames(Type enumType, string[] expectedNames)
    {
        Enum.GetNames(enumType).Should().BeEquivalentTo(expectedNames,
            $"enum {enumType.Name} members are stored as strings in the DB. " +
            "If you renamed one, create a DB migration to UPDATE the old values; " +
            "if you added or removed one, update this expected list.");
    }
}
