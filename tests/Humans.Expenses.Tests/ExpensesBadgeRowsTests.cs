using AwesomeAssertions;
using Humans.Base.Models.Tables;
using Humans.Expenses.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Expenses.Tests;

public class ExpensesBadgeRowsTests
{
    private static readonly Dictionary<Enum, string> Expected = new()
    {
        [ExpenseReportStatus.Draft] = "bg-secondary",
        [ExpenseReportStatus.Submitted] = "bg-primary",
        [ExpenseReportStatus.CoordinatorEndorsed] = "bg-info text-dark",
        [ExpenseReportStatus.Approved] = "bg-success",
        [ExpenseReportStatus.Withdrawn] = "bg-secondary",
    };

    [HumansFact]
    public void SectionRegistersTheExpectedBadgeClassForEveryMember()
    {
        new Section().Register(new ServiceCollection(), new ConfigurationBuilder().Build());

        foreach (var (value, cssClass) in Expected)
            EnumBadgeMap.For(value).Should().Be(cssClass, because: $"{value.GetType().Name}.{value} is registered by the section");

        foreach (var type in new[] { typeof(ExpenseReportStatus) })
            foreach (var member in Enum.GetValues(type).Cast<Enum>())
                Expected.Should().ContainKey(member, because: $"{type.Name}.{member} needs a badge row, not the grey fallback");
    }
}
