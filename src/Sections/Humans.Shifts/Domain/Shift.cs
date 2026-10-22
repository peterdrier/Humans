using Humans.Shifts.Contracts;
using Humans.Settings.Contracts;
using Humans.Base.Attributes;
using NodaTime;

namespace Humans.Shifts.Domain;

/// <summary>Absolute times resolve through the event's calendar, from <see cref="DayOffset"/>.</summary>
internal sealed class Shift
{
    public Guid Id { get; init; }

    public Guid RotaId { get; set; }

    [MarkdownContent]
    public string? Description { get; set; }

    /// <summary>Days from gate opening: negative is build.</summary>
    public int DayOffset { get; set; }

    public LocalTime StartTime { get; set; }

    public Duration Duration { get; set; }

    /// <summary>The understaffed threshold for urgency scoring.</summary>
    public int MinVolunteers { get; set; }

    /// <summary>Hard ceiling: signups and approvals are blocked at it.</summary>
    public int MaxVolunteers { get; set; }

    public bool AdminOnly { get; set; }

    /// <summary>Used by <see cref="GetAbsoluteStart"/> for all-day shifts, whatever <see cref="StartTime"/> holds.</summary>
    internal static readonly LocalTime AllDayWindowStart = new(8, 0);

    /// <summary>Used by <see cref="GetAbsoluteEnd"/> for all-day shifts.</summary>
    internal static readonly LocalTime AllDayWindowEnd = new(18, 0);

    /// <summary>
    /// The worked hours of an all-day shift, used in place of its stored <see cref="Duration"/>, which holds a 24h
    /// sentinel.
    /// </summary>
    internal static double AllDayWindowHours =>
        Duration.FromTicks(AllDayWindowEnd.TickOfDay - AllDayWindowStart.TickOfDay).TotalHours;

    /// <summary>
    /// When true, <see cref="StartTime"/> and <see cref="Duration"/> are don't-care and older rows may hold anything:
    /// read times through <see cref="GetAbsoluteStart"/> / <see cref="GetAbsoluteEnd"/>, never the raw fields.
    /// </summary>
    public bool IsAllDay { get; set; }

    public Instant CreatedAt { get; init; }

    public Instant UpdatedAt { get; set; }

    public Rota Rota { get; set; } = null!;

    public ICollection<ShiftSignup> ShiftSignups { get; } = new List<ShiftSignup>();

    /// <summary>InZoneLeniently, for DST safety.</summary>
    public Instant GetAbsoluteStart(IEventSettingsInfo eventSettings) =>
        ResolveAbsoluteStart(DayOffset, StartTime, IsAllDay, eventSettings);

    /// <summary>
    /// <see cref="GetAbsoluteStart"/> over raw field values, so a flattened shift row resolves
    /// its instants by the same rule as the entity.
    /// </summary>
    internal static Instant ResolveAbsoluteStart(
        int dayOffset, LocalTime startTime, bool isAllDay, IEventSettingsInfo eventSettings)
    {
        var tz = DateTimeZoneProviders.Tzdb[eventSettings.TimeZoneId];
        var date = eventSettings.GateOpeningDate.PlusDays(dayOffset);
        var wallTime = isAllDay ? AllDayWindowStart : startTime;
        return date.At(wallTime).InZoneLeniently(tz).ToInstant();
    }

    public Instant GetAbsoluteEnd(IEventSettingsInfo eventSettings) =>
        ResolveAbsoluteEnd(DayOffset, StartTime, Duration, IsAllDay, eventSettings);

    /// <summary><see cref="GetAbsoluteEnd"/> over raw field values.</summary>
    internal static Instant ResolveAbsoluteEnd(
        int dayOffset, LocalTime startTime, Duration duration, bool isAllDay, IEventSettingsInfo eventSettings)
    {
        if (isAllDay)
        {
            var tz = DateTimeZoneProviders.Tzdb[eventSettings.TimeZoneId];
            var date = eventSettings.GateOpeningDate.PlusDays(dayOffset);
            return date.At(AllDayWindowEnd).InZoneLeniently(tz).ToInstant();
        }
        return ResolveAbsoluteStart(dayOffset, startTime, isAllDay, eventSettings).Plus(duration);
    }

    /// <summary>
    /// All-day shifts always qualify; timed shifts need at least 6 hours. See
    /// <c>src/Sections/Humans.Cantina/Docs/features/dietary-nudge.md</c>.
    /// </summary>
    public bool QualifiesForCantinaMeal() =>
        IsAllDay || Duration >= Duration.FromHours(6);

    public bool IsEarlyEntry => DayOffset < 0;

    public ShiftPeriod GetShiftPeriod(IEventSettingsInfo eventSettings) =>
        ResolveShiftPeriod(DayOffset, eventSettings);

    /// <summary><see cref="GetShiftPeriod"/> over a raw day offset.</summary>
    internal static ShiftPeriod ResolveShiftPeriod(int dayOffset, IEventSettingsInfo eventSettings) =>
        dayOffset < 0 ? ShiftPeriod.Build :
        dayOffset <= eventSettings.EventEndOffset ? ShiftPeriod.Event :
        ShiftPeriod.Strike;
}
