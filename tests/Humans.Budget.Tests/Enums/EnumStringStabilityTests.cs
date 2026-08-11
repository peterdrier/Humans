using AwesomeAssertions;
using Humans.Budget.Contracts;
using Xunit;

namespace Humans.Budget.Tests.Enums;

/// <summary>
/// Budget's half of the string-stored-enum guard. The rest live in
/// <c>Humans.Domain.Tests.Enums.EnumStringStabilityTests</c>; these two moved out with the
/// section because <see cref="BudgetYearStatus"/> and <see cref="ExpenditureType"/> now sit
/// on Budget's contracts leaf, where <c>Humans.Domain.Tests</c> cannot name them
/// (nobodies-collective/Humans#866).
/// </summary>
/// <remarks>
/// Both are persisted with <c>HasConversion&lt;string&gt;()</c>: renaming a member leaves
/// the OLD string in <c>budget_years.status</c> / <c>budget_categories.expenditure_type</c>.
/// A rename needs a migration that UPDATEs the stored values.
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
            typeof(BudgetYearStatus), ["Draft", "Active", "Closed"]
        },
        {
            typeof(ExpenditureType), ["CapEx", "OpEx"]
        }
    };
}
