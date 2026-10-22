using Humans.Shifts.Contracts;
using NodaTime;

namespace Humans.Shifts.Models;

/// <summary>A selected period ignores explicit dates, so the URL has one source of truth.</summary>
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

    /// <summary>Inclusive; null covers Build through Strike.</summary>
    internal static (LocalDate From, LocalDate To) ResolvePeriodRange(ShiftPeriod? period, BurnSettingsInfo es) =>
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
