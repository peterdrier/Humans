using AwesomeAssertions;
using Humans.Shifts.Contracts;
using Humans.Shifts.Models;
using NodaTime;

namespace Humans.Shifts.Tests.Models;

public sealed class ShiftFilterResolverTests
{
    [HumansFact]
    public void Period_NotSet_DatesAreActive()
    {
        var (start, end) = ShiftFilterResolver.Resolve(
            period: null,
            filterStartDate: new LocalDate(2026, 7, 7),
            filterEndDate: new LocalDate(2026, 7, 12));

        start.Should().Be(new LocalDate(2026, 7, 7));
        end.Should().Be(new LocalDate(2026, 7, 12));
    }

    [HumansFact]
    public void Period_Set_DatesNulledOut()
    {
        var (start, end) = ShiftFilterResolver.Resolve(
            period: ShiftPeriod.Event,
            filterStartDate: new LocalDate(2026, 7, 7),
            filterEndDate: new LocalDate(2026, 7, 12));

        start.Should().BeNull();
        end.Should().BeNull();
    }

    [HumansFact]
    public void Nothing_Set_ReturnsNullNull()
    {
        var (start, end) = ShiftFilterResolver.Resolve(
            period: null,
            filterStartDate: null,
            filterEndDate: null);

        start.Should().BeNull();
        end.Should().BeNull();
    }

    [HumansFact]
    public void ResolvePeriodRange_Build_ReturnsBuildWindow()
    {
        // Gate opens 2026-07-09; BuildStartOffset = -7; EventEndOffset = 4; StrikeEndOffset = 6
        // Build = gate-7 .. gate-1
        var es = MakeEventSettings(gate: new LocalDate(2026, 7, 9), buildStart: -7, eventEnd: 4, strikeEnd: 6);
        var (from, to) = ShiftFilterResolver.ResolvePeriodRange(ShiftPeriod.Build, es);
        from.Should().Be(new LocalDate(2026, 7, 2));
        to.Should().Be(new LocalDate(2026, 7, 8));
    }

    [HumansFact]
    public void ResolvePeriodRange_Event_ReturnsEventWindow()
    {
        var es = MakeEventSettings(gate: new LocalDate(2026, 7, 9), buildStart: -7, eventEnd: 4, strikeEnd: 6);
        var (from, to) = ShiftFilterResolver.ResolvePeriodRange(ShiftPeriod.Event, es);
        from.Should().Be(new LocalDate(2026, 7, 9));
        to.Should().Be(new LocalDate(2026, 7, 13));
    }

    [HumansFact]
    public void ResolvePeriodRange_Strike_ReturnsStrikeWindow()
    {
        var es = MakeEventSettings(gate: new LocalDate(2026, 7, 9), buildStart: -7, eventEnd: 4, strikeEnd: 6);
        var (from, to) = ShiftFilterResolver.ResolvePeriodRange(ShiftPeriod.Strike, es);
        from.Should().Be(new LocalDate(2026, 7, 14));
        to.Should().Be(new LocalDate(2026, 7, 15));
    }

    // Only the four calendar scalars ResolvePeriodRange reads are meaningful here;
    // the rest are filled with inert defaults.
    private static BurnSettingsInfo MakeEventSettings(LocalDate gate, int buildStart, int eventEnd, int strikeEnd) =>
        new(
            Id: Guid.NewGuid(),
            EventName: "Test Burn",
            Year: gate.Year,
            TimeZoneId: "Europe/Madrid",
            GateOpeningDate: gate,
            BuildStartOffset: buildStart,
            EventEndOffset: eventEnd,
            StrikeEndOffset: strikeEnd,
            FirstCrewStartOffset: buildStart,
            SetupWeekStartOffset: buildStart,
            PreEventWeekStartOffset: buildStart,
            FinishingWeekendStartOffset: buildStart,
            EarlyEntryCapacity: new Dictionary<int, int>(),
            BarriosEarlyEntryAllocation: null,
            EarlyEntryClose: null,
            IsShiftBrowsingOpen: false);
}
