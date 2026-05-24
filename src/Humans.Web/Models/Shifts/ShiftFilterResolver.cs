using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Web.Models.Shifts;

/// <summary>
/// Server-side period↔date-range mutex (Shifts.md L237).
/// Dates filter only when period is null; once a preset period is selected,
/// explicit dates are ignored so the URL has one source of truth.
/// </summary>
public static class ShiftFilterResolver
{
    public static (LocalDate? activeStart, LocalDate? activeEnd) Resolve(
        ShiftPeriod? period, LocalDate? filterStartDate, LocalDate? filterEndDate)
    {
        var datesAreFilter = !period.HasValue && (filterStartDate.HasValue || filterEndDate.HasValue);
        return (
            datesAreFilter ? filterStartDate : null,
            datesAreFilter ? filterEndDate : null);
    }

    /// <summary>
    /// Maps a preset period to its concrete date range on a given event.
    /// Mirrors the duplicated switches in <c>ShiftBrowsePageBuilder.GetPeriodDateRange</c>
    /// and <c>ShiftsController.GetPeriodDateRange</c> (consolidating those into this single
    /// home is intentional — see CLAUDE.md DRY rule).
    /// </summary>
    public static (LocalDate From, LocalDate To) ResolvePeriodRange(ShiftPeriod period, EventSettings es) =>
        period switch
        {
            ShiftPeriod.Build => (
                es.GateOpeningDate.PlusDays(es.BuildStartOffset),
                es.GateOpeningDate.PlusDays(-1)),
            ShiftPeriod.Event => (
                es.GateOpeningDate,
                es.GateOpeningDate.PlusDays(es.EventEndOffset)),
            ShiftPeriod.Strike => (
                es.GateOpeningDate.PlusDays(es.EventEndOffset + 1),
                es.GateOpeningDate.PlusDays(es.StrikeEndOffset)),
            _ => (
                es.GateOpeningDate.PlusDays(es.BuildStartOffset),
                es.GateOpeningDate.PlusDays(es.StrikeEndOffset))
        };
}
