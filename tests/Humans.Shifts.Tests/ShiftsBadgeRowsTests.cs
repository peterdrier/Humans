using AwesomeAssertions;
using Humans.Base.Models.Tables;
using Humans.Shifts.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Shifts.Tests;

public class ShiftsBadgeRowsTests
{
    private static readonly Dictionary<Enum, string> Expected = new()
    {
        [ShiftPeriod.Build] = "bg-info",
        [ShiftPeriod.Event] = "bg-success",
        [ShiftPeriod.Strike] = "bg-secondary",
        [SignupStatus.Pending] = "bg-warning text-dark",
        [SignupStatus.Confirmed] = "bg-success",
        [SignupStatus.Refused] = "bg-danger",
        [SignupStatus.Bailed] = "bg-secondary",
        [SignupStatus.Cancelled] = "bg-dark",
        [SignupStatus.NoShow] = "bg-danger",
        [RotaPeriod.Build] = "bg-info",
        [RotaPeriod.Event] = "bg-success",
        [RotaPeriod.Strike] = "bg-secondary",
        [RotaPeriod.All] = "bg-primary",
        [ShiftPriority.Normal] = "bg-secondary",
        [ShiftPriority.Important] = "bg-warning text-dark",
        [ShiftPriority.Essential] = "bg-danger",
    };

    [HumansFact]
    public void SectionRegistersTheExpectedBadgeClassForEveryMember()
    {
        new Section().Register(new ServiceCollection(), new ConfigurationBuilder().Build());

        foreach (var (value, cssClass) in Expected)
            EnumBadgeMap.For(value).Should().Be(cssClass, because: $"{value.GetType().Name}.{value} is registered by the section");

        foreach (var type in new[] { typeof(ShiftPeriod), typeof(SignupStatus), typeof(RotaPeriod), typeof(ShiftPriority) })
            foreach (var member in Enum.GetValues(type).Cast<Enum>())
                Expected.Should().ContainKey(member, because: $"{type.Name}.{member} needs a badge row, not the grey fallback");
    }
}
