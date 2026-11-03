using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Localization;
using Humans.Calendar.Services;
using NodaTime;

namespace Humans.Calendar.Contracts;

/// <summary>
/// The fields of the shared create/edit calendar-item form (<c>_CalendarEventForm</c>). A section's own form view model
/// implements it, adds its extras around the partial, and resolves the posted times with
/// <see cref="CalendarEventForm.Resolve"/>. Field names bind at the top level, so the partial takes no prefix.
/// </summary>
public interface ICalendarEventForm
{
    string Title { get; set; }

    string? Description { get; set; }

    string? Location { get; set; }

    string? LocationUrl { get; set; }

    bool IsAllDay { get; set; }

    /// <summary>All-day: the first day. Timed: the start's date in <see cref="TimeZoneId"/>.</summary>
    LocalDate? StartDate { get; set; }

    LocalTime? StartTime { get; set; }

    /// <summary>All-day: the last day, inclusive. Timed: the end's date in <see cref="TimeZoneId"/>.</summary>
    LocalDate? EndDate { get; set; }

    LocalTime? EndTime { get; set; }

    /// <summary>IANA id the timed fields are read in.</summary>
    string TimeZoneId { get; set; }

    bool IsRecurring { get; set; }

    string? RecurrenceRule { get; set; }

    /// <summary>False hides the all-day toggle and makes every item timed.</summary>
    bool SupportsAllDay { get; }

    /// <summary>False hides the recurrence picker and resolves no rule.</summary>
    bool SupportsRecurrence { get; }
}

/// <summary>
/// What a resolved <see cref="ICalendarEventForm"/> stores. Timed items set the instants; all-day items set the
/// floating dates, with an exclusive end. <see cref="RecurrenceTimezone"/> is set only for a timed recurring item.
/// </summary>
public sealed record CalendarEventTimes(
    Instant? Start,
    Instant? End,
    LocalDate? StartDate,
    LocalDate? EndDateExclusive,
    string? RecurrenceRule,
    string? RecurrenceTimezone);

/// <summary>The one place a posted <see cref="ICalendarEventForm"/> becomes stored times, and stored times a form.</summary>
public static class CalendarEventForm
{
    /// <summary>Null when the posted text names no IANA zone, including a blank one.</summary>
    public static DateTimeZone? TryResolveZone(string? tz) =>
        string.IsNullOrWhiteSpace(tz) ? null : DateTimeZoneProviders.Tzdb.GetZoneOrNull(tz);

    /// <summary>Fills the timed fields with <paramref name="start"/>..<paramref name="end"/> shown in <paramref name="zone"/>.</summary>
    public static void SetTimed(ICalendarEventForm form, Instant start, Instant end, DateTimeZone zone)
    {
        ArgumentNullException.ThrowIfNull(form);
        var s = start.InZone(zone).LocalDateTime;
        var e = end.InZone(zone).LocalDateTime;
        form.IsAllDay = false;
        form.StartDate = s.Date;
        form.StartTime = s.TimeOfDay;
        form.EndDate = e.Date;
        form.EndTime = e.TimeOfDay;
        form.TimeZoneId = zone.Id;
    }

    /// <summary>Fills the all-day fields; <paramref name="endExclusive"/> is the stored exclusive end.</summary>
    public static void SetAllDay(ICalendarEventForm form, LocalDate start, LocalDate endExclusive)
    {
        ArgumentNullException.ThrowIfNull(form);
        form.IsAllDay = true;
        form.StartDate = start;
        form.EndDate = CalendarService.AllDayInclusiveEndDate(endExclusive);
    }

    /// <summary>
    /// Resolves the posted fields, adding a field error to <paramref name="modelState"/> for each problem. Null when
    /// any was added. Bad input never throws.
    /// </summary>
    public static CalendarEventTimes? Resolve(
        ICalendarEventForm form, ModelStateDictionary modelState, IStringLocalizer<CalendarResource> localizer)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(modelState);
        ArgumentNullException.ThrowIfNull(localizer);

        var rule = form.SupportsRecurrence && form.IsRecurring && !string.IsNullOrWhiteSpace(form.RecurrenceRule)
            ? form.RecurrenceRule.Trim() : null;
        var allDay = form.SupportsAllDay && form.IsAllDay;
        if (!CalendarRecurrence.IsValidRule(rule))
        {
            modelState.AddModelError(nameof(ICalendarEventForm.RecurrenceRule),
                localizer[allDay ? "Calendar_InvalidAllDayRecurrence" : "Calendar_InvalidTimedRecurrence"]);
            return null;
        }
        return allDay && form.IsAllDay
            ? ResolveAllDay(form, modelState, localizer, rule)
            : ResolveTimed(form, modelState, localizer, rule);
    }

    private static CalendarEventTimes? ResolveAllDay(ICalendarEventForm form, ModelStateDictionary modelState,
        IStringLocalizer<CalendarResource> localizer, string? rule)
    {
        // The hidden time and zone inputs still post; their binding errors are not the member's problem.
        modelState.Remove(nameof(ICalendarEventForm.TimeZoneId));
        modelState.Remove(nameof(ICalendarEventForm.StartTime));
        modelState.Remove(nameof(ICalendarEventForm.EndTime));
        if (form.StartDate is not { } startDate)
        {
            modelState.AddModelError(nameof(ICalendarEventForm.StartDate), localizer["Calendar_StartDateRequired"]);
            return null;
        }
        var first = startDate;
        var last = form.EndDate ?? first;
        if (last < first)
        {
            modelState.AddModelError(nameof(ICalendarEventForm.EndDate), localizer["Calendar_EndDateBeforeStart"]);
            return null;
        }
        if (last == LocalDate.MaxIsoValue)
        {
            modelState.AddModelError(nameof(ICalendarEventForm.EndDate), localizer["Calendar_InvalidAllDayEvent"]);
            return null;
        }
        var (startStored, endExclusive) = CalendarService.AllDayWindow(first, last);
        return new CalendarEventTimes(null, null, startStored, endExclusive, rule, null);
    }

    private static CalendarEventTimes? ResolveTimed(ICalendarEventForm form, ModelStateDictionary modelState,
        IStringLocalizer<CalendarResource> localizer, string? rule)
    {
        var zone = TryResolveZone(form.TimeZoneId);
        if (zone is null && modelState.GetFieldValidationState(nameof(ICalendarEventForm.TimeZoneId)) == ModelValidationState.Invalid)
            return null;
        if (zone is null)
        {
            modelState.AddModelError(nameof(ICalendarEventForm.TimeZoneId), localizer["Calendar_UnknownTimezone"]);
            return null;
        }
        if (form.StartDate is not { } startDate || form.StartTime is not { } startTime)
        {
            modelState.AddModelError(nameof(ICalendarEventForm.StartTime), localizer["Calendar_StartRequired"]);
            return null;
        }
        if (form.EndTime is not { } endTime)
        {
            modelState.AddModelError(nameof(ICalendarEventForm.EndTime), localizer["Calendar_EndRequired"]);
            return null;
        }
        var start = startDate.At(startTime).InZoneLeniently(zone).ToInstant();
        var end = (form.EndDate ?? startDate).At(endTime).InZoneLeniently(zone).ToInstant();
        if (end < start)
        {
            modelState.AddModelError(nameof(ICalendarEventForm.EndTime), localizer["Calendar_EndBeforeStart"]);
            return null;
        }
        return new CalendarEventTimes(start, end, null, null, rule, rule is null ? null : zone.Id);
    }
}
