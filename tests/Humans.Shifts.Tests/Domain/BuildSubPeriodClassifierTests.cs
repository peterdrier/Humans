using Humans.Shifts.Services;
using Humans.Settings.Contracts;
using AwesomeAssertions;
using Xunit;

using Humans.Shifts.Contracts;
namespace Humans.Shifts.Tests.Domain;

public class BuildSubPeriodClassifierTests
{
    private static BurnSettingsInfo DefaultSettings() => new(
        Id: Guid.NewGuid(),
        EventName: "Test Event",
        Year: 2026,
        TimeZoneId: "Europe/Madrid",
        GateOpeningDate: default,
        // Default boundaries match the org convention shipped via EF HasDefaultValue.
        BuildStartOffset: -25,
        EventEndOffset: 6,
        StrikeEndOffset: 9,
        FirstCrewStartOffset: -25,
        SetupWeekStartOffset: -16,
        PreEventWeekStartOffset: -9,
        FinishingWeekendStartOffset: -4,
        EarlyEntryCapacity: new Dictionary<int, int>(),
        BarriosEarlyEntryAllocation: null,
        EarlyEntryClose: null, IsShiftBrowsingOpen: false);

    [HumansTheory]
    [InlineData(-25, nameof(BuildSubPeriod.FirstCrew))]
    [InlineData(-17, nameof(BuildSubPeriod.FirstCrew))]
    [InlineData(-16, nameof(BuildSubPeriod.SetupWeek))]
    [InlineData(-10, nameof(BuildSubPeriod.SetupWeek))]
    [InlineData(-9, nameof(BuildSubPeriod.PreEventWeek))]
    [InlineData(-5, nameof(BuildSubPeriod.PreEventWeek))]
    [InlineData(-4, nameof(BuildSubPeriod.FinishingWeekend))]
    [InlineData(-1, nameof(BuildSubPeriod.FinishingWeekend))]
    public void Classify_returns_expected_subperiod(int dayOffset, string expected)
    {
        BuildSubPeriodClassifier.Classify(dayOffset, DefaultSettings()).Should().Be(Enum.Parse<BuildSubPeriod>(expected));
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
        var settings = DefaultSettings() with { FirstCrewStartOffset = -20 };
        // -25 predates the FirstCrew boundary at -20 → unclassified ("pre-build").
        BuildSubPeriodClassifier.Classify(-25, settings).Should().BeNull();
    }

    [HumansTheory]
    [InlineData(nameof(BuildSubPeriod.FirstCrew), -25, -16)]
    [InlineData(nameof(BuildSubPeriod.SetupWeek), -16, -9)]
    [InlineData(nameof(BuildSubPeriod.PreEventWeek), -9, -4)]
    [InlineData(nameof(BuildSubPeriod.FinishingWeekend), -4, 0)]
    public void BoundsFor_returns_half_open_range(string sub, int expectedStart, int expectedEnd)
    {
        var (start, end) = BuildSubPeriodClassifier.BoundsFor(Enum.Parse<BuildSubPeriod>(sub), DefaultSettings());
        start.Should().Be(expectedStart);
        end.Should().Be(expectedEnd);
    }

    [HumansFact]
    public void BoundsFor_with_inverted_offsets_returns_empty_range_silently()
    {
        // Mis-ordered offsets give an empty range and no exception. Only the ordering guard on
        // EventSettingsViewModel prevents it; this pins that the classifier itself has none.
        var inverted = DefaultSettings() with
        {
            FirstCrewStartOffset = -10, // inverted: later than SetupWeekStartOffset
        };
        var (start, end) = BuildSubPeriodClassifier.BoundsFor(BuildSubPeriod.FirstCrew, inverted);
        // start (-10) >= end (-16) → the range is empty; callers get zero days.
        start.Should().BeGreaterThanOrEqualTo(end);
    }
}
