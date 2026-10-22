using AwesomeAssertions;
using Humans.Governance.Contracts;
using Humans.Governance.Domain;
using Xunit;

namespace Humans.Governance.Tests.Enums;

/// <summary>
/// All three are persisted with <c>HasConversion&lt;string&gt;()</c>: renaming a member leaves the
/// OLD string in the column. A rename needs a migration that UPDATEs the stored values.
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
            typeof(ApplicationStatus), ["Submitted", "Approved", "Rejected", "Withdrawn"]
        },
        {
            typeof(VoteChoice), ["Yay", "Maybe", "No", "Abstain"]
        },
        {
            // applications.MembershipTier and governance_membership_tiers.Tier.
            typeof(MembershipTier), ["Volunteer", "Colaborador", "Asociado"]
        }
    };
}
