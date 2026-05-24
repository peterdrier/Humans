using AwesomeAssertions;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Models.Shifts;
using NodaTime;

namespace Humans.Web.Tests.Models.Shifts;

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

    // EventSettings is a plain property-bag entity (see Humans.Domain.Entities.EventSettings) —
    // existing tests use the object-initializer pattern, e.g.
    //   tests/Humans.Application.Tests/Services/Shifts/ShiftManagementServiceCoveragePiesTests.cs:346
    // We mirror that here.
    private static EventSettings MakeEventSettings(LocalDate gate, int buildStart, int eventEnd, int strikeEnd) =>
        new()
        {
            GateOpeningDate = gate,
            BuildStartOffset = buildStart,
            EventEndOffset = eventEnd,
            StrikeEndOffset = strikeEnd
        };
}
