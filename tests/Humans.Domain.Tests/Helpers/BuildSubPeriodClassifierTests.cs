using AwesomeAssertions;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Domain.Helpers;
using Xunit;

namespace Humans.Domain.Tests.Helpers;

public class BuildSubPeriodClassifierTests
{
    private static EventSettings DefaultSettings() => new()
    {
        // Default boundaries match the org convention shipped via EF HasDefaultValue.
        BuildStartOffset = -25,
        FirstCrewStartOffset = -25,
        SetupWeekStartOffset = -16,
        PreEventWeekStartOffset = -9,
        FinishingWeekendStartOffset = -4,
    };

    [HumansTheory]
    [InlineData(-25, BuildSubPeriod.FirstCrew)]
    [InlineData(-17, BuildSubPeriod.FirstCrew)]
    [InlineData(-16, BuildSubPeriod.SetupWeek)]
    [InlineData(-10, BuildSubPeriod.SetupWeek)]
    [InlineData(-9, BuildSubPeriod.PreEventWeek)]
    [InlineData(-5, BuildSubPeriod.PreEventWeek)]
    [InlineData(-4, BuildSubPeriod.FinishingWeekend)]
    [InlineData(-1, BuildSubPeriod.FinishingWeekend)]
    public void Classify_returns_expected_subperiod(int dayOffset, BuildSubPeriod expected)
    {
        BuildSubPeriodClassifier.Classify(dayOffset, DefaultSettings()).Should().Be(expected);
    }

    [HumansTheory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(10)]
    public void Classify_returns_null_for_event_or_strike_offsets(int dayOffset)
    {
        BuildSubPeriodClassifier.Classify(dayOffset, DefaultSettings()).Should().BeNull();
    }

    [HumansFact]
    public void Classify_returns_null_for_offset_before_first_crew_boundary()
    {
        var settings = DefaultSettings();
        settings.FirstCrewStartOffset = -20;
        // -25 predates the FirstCrew boundary at -20 → unclassified ("pre-build").
        BuildSubPeriodClassifier.Classify(-25, settings).Should().BeNull();
    }

    [HumansTheory]
    [InlineData(BuildSubPeriod.FirstCrew, -25, -16)]
    [InlineData(BuildSubPeriod.SetupWeek, -16, -9)]
    [InlineData(BuildSubPeriod.PreEventWeek, -9, -4)]
    [InlineData(BuildSubPeriod.FinishingWeekend, -4, 0)]
    public void BoundsFor_returns_half_open_range(BuildSubPeriod sub, int expectedStart, int expectedEnd)
    {
        var (start, end) = BuildSubPeriodClassifier.BoundsFor(sub, DefaultSettings());
        start.Should().Be(expectedStart);
        end.Should().Be(expectedEnd);
    }
}
