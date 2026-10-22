using AwesomeAssertions;
using Humans.Shifts.Contracts;
using Xunit;

namespace Humans.Shifts.Tests.Enums;

/// <summary>
/// All four are persisted with <c>HasConversion&lt;string&gt;()</c>: renaming a member leaves
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
            typeof(ShiftPriority), ["Normal", "Important", "Essential"]
        },
        {
            typeof(SignupPolicy), ["Public", "RequireApproval"]
        },
        {
            typeof(SignupStatus), ["Pending", "Confirmed", "Refused", "Bailed", "Cancelled", "NoShow"]
        },
        {
            typeof(RotaPeriod), ["Build", "Event", "Strike", "All"]
        }
    };
}
