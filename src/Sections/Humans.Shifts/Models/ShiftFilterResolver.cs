using Humans.Shifts.Contracts;
using Humans.Shifts.Helpers;
using NodaTime;

namespace Humans.Shifts.Models;

/// <summary>
/// Server-side period↔date-range mutex.
/// Dates filter only when period is null; once a preset period is selected,
/// explicit dates are ignored so the URL has one source of truth.
/// </summary>
internal static class ShiftFilterResolver
{
    internal static (LocalDate? activeStart, LocalDate? activeEnd) Resolve(
        ShiftPeriod? period, LocalDate? filterStartDate, LocalDate? filterEndDate)
    {
        var datesAreFilter = !period.HasValue && (filterStartDate.HasValue || filterEndDate.HasValue);
        return (
            datesAreFilter ? filterStartDate : null,
            datesAreFilter ? filterEndDate : null);
    }

    /// <summary>
    /// Maps a preset period to its concrete date range on a given event.
    /// </summary>
    internal static (LocalDate From, LocalDate To) ResolvePeriodRange(ShiftPeriod period, BurnSettingsInfo es) =>
        period switch
        {
            ShiftPeriod.Build => (
                es.DateForOffset(es.BuildStartOffset),
                es.DateForOffset(-1)),
            ShiftPeriod.Event => (
                es.DateForOffset(0),
                es.DateForOffset(es.EventEndOffset)),
            ShiftPeriod.Strike => (
                es.DateForOffset(es.EventEndOffset + 1),
                es.DateForOffset(es.StrikeEndOffset)),
            _ => (
                es.DateForOffset(es.BuildStartOffset),
                es.DateForOffset(es.StrikeEndOffset))
        };
}
