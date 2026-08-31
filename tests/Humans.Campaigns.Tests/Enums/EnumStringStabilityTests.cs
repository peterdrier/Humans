using AwesomeAssertions;
using Humans.Campaigns.Domain;

namespace Humans.Campaigns.Tests.Enums;

/// <summary>
/// Campaigns' string-stored-enum guard. Lives here rather than the central
/// <c>Humans.Domain.Tests.Enums.EnumStringStabilityTests</c> because <see cref="CampaignStatus"/>
/// is internal to <c>Humans.Campaigns</c> after the section's G5 move
/// (nobodies-collective/Humans#866), so the central project cannot name it
/// (nobodies-collective/Humans#1025).
/// </summary>
/// <remarks>
/// Persisted with <c>HasConversion&lt;string&gt;()</c> / <c>HasMaxLength(20)</c>: renaming a
/// member leaves the OLD string in <c>campaigns.status</c>. A rename needs a migration that
/// UPDATEs the stored values. A <c>[Fact]</c> rather than a theory: a public test method
/// cannot take an internal enum type as a parameter (CS0051).
/// </remarks>
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
