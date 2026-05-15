using AwesomeAssertions;
using Humans.Domain.Enums;
using Humans.Web.Controllers;
using NodaTime;

namespace Humans.Web.Tests.Controllers;

/// <summary>
/// Covers <c>Shifts.md</c> invariant line 237 (dashboard filter period↔date-range
/// mutex): when both period and dates arrive on a single request, period wins for
/// filtering. The server defends this via
/// <see cref="ShiftDashboardController.ResolveActiveDateRange"/> — when a period
/// is selected, the dates are zeroed out before being passed to the urgent-shifts
/// query, so the date inputs round-trip back to the form but are not applied
/// as bounds.
/// </summary>
public class ShiftDashboardControllerTests
{
    [HumansFact]
    public void ResolveActiveDateRange_BothPeriodAndDatesProvided_PeriodWins_DatesIgnored()
    {
        // Arrange: both a period AND a date range are present (e.g. crafted URL
        // or stale browser state where JS didn't clear one side).
        ShiftPeriod? period = ShiftPeriod.Event;
        LocalDate? start = new LocalDate(2026, 7, 5);
        LocalDate? end = new LocalDate(2026, 7, 8);

        // Act
        var (activeStart, activeEnd) = ShiftDashboardController.ResolveActiveDateRange(
            period, start, end);

        // Assert: dates are dropped — period is the filter; dates round-trip
        // visually but do not bound the query.
        activeStart.Should().BeNull();
        activeEnd.Should().BeNull();
    }

    [HumansFact]
    public void ResolveActiveDateRange_OnlyDatesProvided_DatesAreFilter()
    {
        // Arrange: no period selected, only dates.
        ShiftPeriod? period = null;
        LocalDate? start = new LocalDate(2026, 7, 5);
        LocalDate? end = new LocalDate(2026, 7, 8);

        // Act
        var (activeStart, activeEnd) = ShiftDashboardController.ResolveActiveDateRange(
            period, start, end);

        // Assert: dates are the active filter.
        activeStart.Should().Be(start);
        activeEnd.Should().Be(end);
    }

    [HumansFact]
    public void ResolveActiveDateRange_OnlyPeriodProvided_DatesNull()
    {
        // Arrange: pure period selection.
        ShiftPeriod? period = ShiftPeriod.Build;
        LocalDate? start = null;
        LocalDate? end = null;

        // Act
        var (activeStart, activeEnd) = ShiftDashboardController.ResolveActiveDateRange(
            period, start, end);

        // Assert: dates stay null (period filters).
        activeStart.Should().BeNull();
        activeEnd.Should().BeNull();
    }
}
