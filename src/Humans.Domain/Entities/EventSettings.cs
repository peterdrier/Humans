using NodaTime;

namespace Humans.Domain.Entities;

/// <summary>
/// Singleton event configuration — dates, timezone, early entry capacity, and global caps.
/// One active EventSettings per event cycle.
/// </summary>
public class EventSettings
{
    /// <summary>
    /// Unique identifier.
    /// </summary>
    public Guid Id { get; init; }

    /// <summary>
    /// Display name for this event (e.g., "Nowhere 2026").
    /// </summary>
    public string EventName { get; set; } = string.Empty;

    /// <summary>
    /// The year of this event (e.g., 2026).
    /// </summary>
    public int Year { get; set; }

    /// <summary>
    /// IANA timezone ID (e.g., "Europe/Madrid").
    /// </summary>
    public string TimeZoneId { get; set; } = string.Empty;

    /// <summary>
    /// The date gates open — DayOffset 0.
    /// </summary>
    public LocalDate GateOpeningDate { get; set; }

    /// <summary>
    /// Negative offset for the first build day (e.g., -14).
    /// </summary>
    public int BuildStartOffset { get; set; }

    /// <summary>
    /// Offset for the last event day (inclusive). Strike starts at EventEndOffset + 1.
    /// </summary>
    public int EventEndOffset { get; set; }

    /// <summary>
    /// Offset for the last strike day (inclusive).
    /// </summary>
    public int StrikeEndOffset { get; set; }

    // ------------------------------------------------------------------
    // Build-phase sub-period boundaries — the build period is split into
    // four named sub-periods so the shift dashboard can filter per phase.
    // Each offset is the inclusive start day of its sub-period; the end is
    // the next sub-period's start (exclusive). All offsets are negative
    // and ascending: BuildStartOffset ≤ FirstCrew ≤ SetupWeek ≤ PreEventWeek
    // ≤ FinishingWeekend < 0. Coordinators reconfigure these per event so
    // the absolute dates auto-shift with GateOpeningDate.
    // ------------------------------------------------------------------

    /// <summary>Inclusive start day of the "First crew" sub-period (default -25).</summary>
    public int FirstCrewStartOffset { get; set; } = -25;

    /// <summary>Inclusive start day of the "Set-up week" sub-period (default -16).</summary>
    public int SetupWeekStartOffset { get; set; } = -16;

    /// <summary>Inclusive start day of the "Pre-event week" sub-period (default -9).</summary>
    public int PreEventWeekStartOffset { get; set; } = -9;

    /// <summary>Inclusive start day of the "Finishing weekend" sub-period (default -4).</summary>
    public int FinishingWeekendStartOffset { get; set; } = -4;

    /// <summary>
    /// Step function: DayOffset → cumulative EE capacity at that point.
    /// Keys are day offsets, values are total headcount allowed.
    /// </summary>
    public Dictionary<int, int> EarlyEntryCapacity { get; set; } = new();

    /// <summary>
    /// Optional barrios-specific EE allocation (DayOffset → reserved barrios headcount).
    /// Subtracted from general pool when computing available EE slots.
    /// </summary>
    public Dictionary<int, int>? BarriosEarlyEntryAllocation { get; set; }

    /// <summary>
    /// After this instant, non-privileged users cannot sign up for or bail build shifts.
    /// </summary>
    public Instant? EarlyEntryClose { get; set; }

    /// <summary>
    /// Whether the shift browsing system is open to regular volunteers.
    /// </summary>
    public bool IsShiftBrowsingOpen { get; set; }

    /// <summary>
    /// Optional global cap on total volunteer signups per event.
    /// </summary>
    public int? GlobalVolunteerCap { get; set; }

    /// <summary>
    /// Hours before a shift to send reminder notifications.
    /// </summary>
    public int ReminderLeadTimeHours { get; set; } = 24;

    /// <summary>
    /// Whether this is the active event configuration.
    /// </summary>
    public bool IsActive { get; set; }

    /// <summary>
    /// When this record was created.
    /// </summary>
    public Instant CreatedAt { get; init; }

    /// <summary>
    /// When this record was last updated.
    /// </summary>
    public Instant UpdatedAt { get; set; }

    /// <summary>
    /// Navigation property to rotas associated with this event.
    /// </summary>
    public ICollection<Rota> Rotas { get; } = new List<Rota>();

    /// <summary>
    /// Gets the cumulative EE capacity for a given day offset using step function lookup.
    /// Returns the capacity for the largest key ≤ dayOffset, or 0 if no key qualifies.
    /// </summary>
    public int GetEarlyEntryCapacityForDay(int dayOffset)
    {
        if (EarlyEntryCapacity.Count == 0)
            return 0;

        var applicableKey = int.MinValue;
        foreach (var key in EarlyEntryCapacity.Keys)
        {
            if (key <= dayOffset && key > applicableKey)
                applicableKey = key;
        }

        return applicableKey == int.MinValue ? 0 : EarlyEntryCapacity[applicableKey];
    }
}
