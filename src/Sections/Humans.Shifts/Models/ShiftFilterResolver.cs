using Humans.Shifts.Contracts;
using Humans.Shifts.Services;
using NodaTime;
using NodaTime.Text;

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

    internal static (LocalDate Start, LocalDate End)? ResolveExportRange(
        BurnSettingsInfo eventSettings, string? startDate, string? endDate, ShiftPeriod? period, BuildSubPeriod? subPeriod)
    {
        var parsedStart = period.HasValue ? null : TryParseLocalDate(startDate);
        var parsedEnd = period.HasValue ? null : TryParseLocalDate(endDate);
        if (!period.HasValue &&
            ((!string.IsNullOrWhiteSpace(startDate) && parsedStart is null)
             || (!string.IsNullOrWhiteSpace(endDate) && parsedEnd is null)
             || parsedStart.HasValue != parsedEnd.HasValue))
            return null;
        var (activeStart, activeEnd) = Resolve(period, parsedStart, parsedEnd);
        try
        {
            // Build with a sub-period narrows to it; any period uses its window; otherwise explicit dates, else the
            // whole event, Build through Strike.
            LocalDate rangeStart, rangeEnd;
            if (period == ShiftPeriod.Build && subPeriod.HasValue)
            {
                var (startOffset, endExclusive) = BuildSubPeriodClassifier.BoundsFor(subPeriod.Value, eventSettings);
                rangeStart = eventSettings.GateOpeningDate.PlusDays(startOffset);
                rangeEnd = eventSettings.GateOpeningDate.PlusDays(endExclusive - 1);
            }
            else if (period.HasValue)
            {
                (rangeStart, rangeEnd) = ResolvePeriodRange(period.Value, eventSettings);
            }
            else if (activeStart.HasValue && activeEnd.HasValue)
            {
                (rangeStart, rangeEnd) = (activeStart.Value, activeEnd.Value);
            }
            else
            {
                (rangeStart, rangeEnd) = ResolvePeriodRange(null, eventSettings);
            }
            // HTML5 validation blocks this, but the action is reachable directly.
            if (rangeEnd < rangeStart)
            {
                (rangeStart, rangeEnd) = (rangeEnd, rangeStart);
            }
            var zone = DateTimeZoneProviders.Tzdb[eventSettings.TimeZoneId];
            _ = rangeStart.AtStartOfDayInZone(zone).ToInstant();
            _ = rangeEnd.PlusDays(1).AtStartOfDayInZone(zone).ToInstant();
            return (rangeStart, rangeEnd);
        }
        catch (Exception ex) when (ex is OverflowException or SkippedTimeException)
        {
            return null;
        }
    }

    private static LocalDate? TryParseLocalDate(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var parsed = LocalDatePattern.Iso.Parse(input);
        return parsed.Success ? parsed.Value : null;
    }
}
