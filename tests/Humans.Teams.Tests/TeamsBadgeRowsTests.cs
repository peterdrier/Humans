using AwesomeAssertions;
using Humans.Base.Models.Tables;
using Humans.Teams.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Teams.Tests;

public class TeamsBadgeRowsTests
{
    private static readonly Dictionary<Enum, string> Expected = new()
    {
        [SlotPriority.None] = "bg-light text-dark",
        [SlotPriority.Critical] = "bg-danger",
        [SlotPriority.Important] = "bg-warning text-dark",
        [SlotPriority.NiceToHave] = "bg-secondary",
        [RolePeriod.YearRound] = "bg-light text-dark",
        [RolePeriod.Build] = "bg-info",
        [RolePeriod.Event] = "bg-success",
        [RolePeriod.Strike] = "bg-secondary",
    };

    [HumansFact]
    public void SectionRegistersTheExpectedBadgeClassForEveryMember()
    {
        new Section().Register(new ServiceCollection(), new ConfigurationBuilder().Build());

        foreach (var (value, cssClass) in Expected)
            EnumBadgeMap.For(value).Should().Be(cssClass, because: $"{value.GetType().Name}.{value} is registered by the section");

        foreach (var type in new[] { typeof(SlotPriority), typeof(RolePeriod) })
            foreach (var member in Enum.GetValues(type).Cast<Enum>())
                Expected.Should().ContainKey(member, because: $"{type.Name}.{member} needs a badge row, not the grey fallback");
    }
}
