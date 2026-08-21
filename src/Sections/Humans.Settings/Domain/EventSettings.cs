using Humans.Settings.Contracts;
using NodaTime;

namespace Humans.Settings.Domain;

/// <summary>
/// The app-wide event configuration — identity, calendar anchor, phase offsets,
/// time zone and the early-entry window. One active row per event cycle.
/// </summary>
/// <remarks>
/// Section-specific knobs are deliberately absent: the shift-browsing switch,
/// the global volunteer cap and the reminder lead time belong to Shifts and stay
/// on the Shifts-owned row.
/// </remarks>
internal sealed class EventSettings : IEventSettingsInfo
{
    /// <summary>Unique identifier — the id other sections store to point at this cycle.</summary>
    public Guid Id { get; init; }

    /// <summary>Display name for this event (e.g., "Nowhere 2026").</summary>
    public string EventName { get; set; } = string.Empty;

    /// <summary>The year of this event (e.g., 2026).</summary>
    public int Year { get; set; }

    /// <summary>IANA timezone ID (e.g., "Europe/Madrid").</summary>
    public string TimeZoneId { get; set; } = string.Empty;

    /// <summary>The date gates open — DayOffset 0.</summary>
    public LocalDate GateOpeningDate { get; set; }

    /// <summary>Negative offset for the first build day (e.g., -14).</summary>
    public int BuildStartOffset { get; set; }

    /// <summary>Offset for the last event day (inclusive). Strike starts at EventEndOffset + 1.</summary>
    public int EventEndOffset { get; set; }

    /// <summary>Offset for the last strike day (inclusive).</summary>
    public int StrikeEndOffset { get; set; }

    // ------------------------------------------------------------------
    // Build-phase sub-period boundaries — the build period is split into
    // four named sub-periods so the shift dashboard can filter per phase.
    // Each offset is the inclusive start day of its sub-period; the end is
    // the next sub-period's start (exclusive). All offsets are negative
    // and ascending: BuildStartOffset ≤ FirstCrew ≤ SetupWeek ≤ PreEventWeek
    // ≤ FinishingWeekend < 0.
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
    /// Lifecycle of this cycle. Deleting sets <see cref="EventSettingsStatus.Deleted"/>;
    /// the row is never removed, because other sections point at its <see cref="Id"/>.
    /// </summary>
    public EventSettingsStatus Status { get; set; } = EventSettingsStatus.Active;

    /// <summary>When this record was created.</summary>
    public Instant CreatedAt { get; set; }

    /// <summary>When this record was last updated.</summary>
    public Instant UpdatedAt { get; set; }
}
