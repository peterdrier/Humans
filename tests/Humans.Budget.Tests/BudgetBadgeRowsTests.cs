using AwesomeAssertions;
using Humans.Base.Models.Tables;
using Humans.Budget.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Budget.Tests;

public class BudgetBadgeRowsTests
{
    private static readonly Dictionary<Enum, string> Expected = new()
    {
        [BudgetYearStatus.Draft] = "bg-warning text-dark",
        [BudgetYearStatus.Active] = "bg-success",
        [BudgetYearStatus.Closed] = "bg-secondary",
    };

    [HumansFact]
    public void SectionRegistersTheExpectedBadgeClassForEveryMember()
    {
        new Section().Register(new ServiceCollection(), new ConfigurationBuilder().Build());

        foreach (var (value, cssClass) in Expected)
            EnumBadgeMap.For(value).Should().Be(cssClass, because: $"{value.GetType().Name}.{value} is registered by the section");

        foreach (var type in new[] { typeof(BudgetYearStatus) })
            foreach (var member in Enum.GetValues(type).Cast<Enum>())
                Expected.Should().ContainKey(member, because: $"{type.Name}.{member} needs a badge row, not the grey fallback");
    }
}
