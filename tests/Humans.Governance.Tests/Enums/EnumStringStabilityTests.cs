using AwesomeAssertions;
using Humans.Governance.Contracts;
using Humans.Governance.Domain;
using Xunit;

namespace Humans.Governance.Tests.Enums;

/// <summary>
/// Governance's half of the string-stored-enum guard. The rest live in the test project of the
/// section that owns each enum; these two moved out with the
/// section because <c>ApplicationStatus</c> now sits on Governance's contracts leaf and
/// <c>VoteChoice</c> turned internal in the section, neither of which
/// <c>Humans.Domain.Tests</c> can name (nobodies-collective/Humans#866).
/// </summary>
/// <remarks>
/// Both are persisted with <c>HasConversion&lt;string&gt;()</c>: renaming a member leaves the
/// OLD string in <c>applications.status</c> / <c>board_votes.vote</c>. A rename needs a
/// migration that UPDATEs the stored values.
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
            typeof(ApplicationStatus), ["Submitted", "Approved", "Rejected", "Withdrawn"]
        },
        {
            typeof(VoteChoice), ["Yay", "Maybe", "No", "Abstain"]
        }
    };
}
