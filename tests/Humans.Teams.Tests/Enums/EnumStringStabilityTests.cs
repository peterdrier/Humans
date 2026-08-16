using AwesomeAssertions;
using Humans.Teams.Contracts;
using Xunit;

namespace Humans.Teams.Tests.Enums;

/// <summary>
/// Teams' share of the string-stored-enum guard, moved out of
/// <c>Humans.Domain.Tests.Enums.EnumStringStabilityTests</c> when that orphaned project's rows
/// were distributed to their owners —
/// <see cref="TeamMemberRole"/>, <see cref="TeamJoinRequestStatus"/> and
/// <see cref="RolePeriod"/> live on <c>Humans.Teams.Contracts</c>, so the guard belongs to the
/// section that owns them (nobodies-collective/Humans#866).
/// </summary>
/// <remarks>
/// All three are persisted with <c>HasConversion&lt;string&gt;()</c>: renaming a member leaves
/// the OLD string in the column. A rename needs a migration that UPDATEs the stored values.
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
            typeof(TeamMemberRole), ["Member", "Coordinator"]
        },
        {
            typeof(TeamJoinRequestStatus), ["Pending", "Approved", "Rejected", "Withdrawn"]
        },
        {
            typeof(RolePeriod), ["YearRound", "Build", "Event", "Strike"]
        }
    };
}
