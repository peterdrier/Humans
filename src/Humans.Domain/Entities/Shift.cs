using Humans.Domain.Attributes;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Domain.Entities;

/// <summary>
/// A single work slot within a rota — defined by DayOffset from gate opening,
/// start time, and duration. Absolute times are resolved via EventSettings.
/// </summary>
public class Shift
{
    /// <summary>
    /// Unique identifier.
    /// </summary>
    public Guid Id { get; init; }

    /// <summary>
    /// FK to the parent rota.
    /// </summary>
    public Guid RotaId { get; set; }

    /// <summary>
    /// Optional description of shift duties.
    /// </summary>
    [MarkdownContent]
    public string? Description { get; set; }

    /// <summary>
    /// Day offset from gate opening date. Negative = build, 0+ = event/strike.
    /// </summary>
    public int DayOffset { get; set; }

    /// <summary>
    /// Start time of the shift (wall clock in event timezone).
    /// </summary>
    public LocalTime StartTime { get; set; }

    /// <summary>
    /// Duration of the shift.
    /// </summary>
    public Duration Duration { get; set; }

    /// <summary>
    /// Minimum volunteers needed (understaffed threshold for urgency scoring).
    /// </summary>
    public int MinVolunteers { get; set; }

    /// <summary>
    /// Maximum volunteers allowed (hard capacity ceiling — signups and approvals are blocked at this limit).
    /// </summary>
    public int MaxVolunteers { get; set; }

    /// <summary>
    /// Whether this shift is restricted to coordinators/admins only.
    /// </summary>
    public bool AdminOnly { get; set; }

    /// <summary>
    /// Whether this is an all-day shift (build/strike). All-day shifts store
    /// StartTime=00:00, Duration=24h but UI ignores these values.
    /// </summary>
    public bool IsAllDay { get; set; }

    /// <summary>
    /// When this shift was created.
    /// </summary>
    public Instant CreatedAt { get; init; }

    /// <summary>
    /// When this shift was last updated.
    /// </summary>
    public Instant UpdatedAt { get; set; }

    /// <summary>
    /// Navigation property to the parent rota.
    /// </summary>
    public Rota Rota { get; set; } = null!;

    /// <summary>
    /// Navigation property to signups for this shift.
    /// </summary>
    public ICollection<ShiftSignup> ShiftSignups { get; } = new List<ShiftSignup>();

    /// <summary>
    /// Resolves the absolute start instant using event settings timezone and gate opening date.
    /// Uses InZoneLeniently for DST safety.
    /// </summary>
    public Instant GetAbsoluteStart(EventSettings eventSettings)
    {
        var tz = DateTimeZoneProviders.Tzdb[eventSettings.TimeZoneId];
        var date = eventSettings.GateOpeningDate.PlusDays(DayOffset);
        return date.At(StartTime).InZoneLeniently(tz).ToInstant();
    }

    /// <summary>
    /// Resolves the absolute end instant (start + duration).
    /// </summary>
    public Instant GetAbsoluteEnd(EventSettings eventSettings) =>
        GetAbsoluteStart(eventSettings).Plus(Duration);

    /// <summary>
    /// Whether this shift falls in the build period (before gate opening).
    /// </summary>
    public bool IsEarlyEntry => DayOffset < 0;

    /// <summary>
    /// Classifies the shift into Build, Event, or Strike period based on its day offset.
    /// </summary>
    public ShiftPeriod GetShiftPeriod(EventSettings eventSettings) =>
        DayOffset < 0 ? ShiftPeriod.Build :
        DayOffset <= eventSettings.EventEndOffset ? ShiftPeriod.Event :
        ShiftPeriod.Strike;
}
