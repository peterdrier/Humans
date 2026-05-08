using NodaTime;

namespace Humans.Domain.Entities;

/// <summary>
/// Per-user, per-event Shifts-owned coordination state used by the Volunteer
/// Tracking page: optional camp-set-up start date plus a list of day offsets
/// the volunteer is blocked out (doctor visit, rest day, etc.).
///
/// One row per (UserId, EventSettingsId). A row with BarrioSetupStartDate=null
/// and empty BlockedDayOffsets is functionally equivalent to no row.
/// </summary>
public class VolunteerBuildStatus
{
    public Guid Id { get; init; }

    /// <summary>
    /// Cross-section linkage — bare Guid (no nav property, no HasOne) per
    /// memory/architecture/no-cross-section-ef-joins.md.
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>Same-section FK to event_settings.</summary>
    public Guid EventSettingsId { get; set; }

    /// <summary>
    /// Calendar date the volunteer left for barrio set-up. Null = not yet
    /// on set-up. When set, days at this offset and later render as
    /// CampSetup in the heatmap and never count as gaps.
    /// </summary>
    public LocalDate? BarrioSetupStartDate { get; set; }

    /// <summary>
    /// Day offsets the volunteer is blocked out (doctor, rest day, etc.).
    /// Stored sorted, deduped. Always inside [BuildStartOffset, 0).
    /// jsonb column; pattern matches GeneralAvailability.AvailableDayOffsets.
    /// </summary>
    public List<int> BlockedDayOffsets { get; set; } = new();

    /// <summary>
    /// Optional free-text from the coordinator who set/cleared the
    /// camp set-up date. Block edits do NOT touch this field.
    /// </summary>
    public string? Notes { get; set; }

    /// <summary>
    /// Coordinator who last modified BarrioSetupStartDate. Block edits do
    /// NOT touch this field — block audit trail lives in audit_log.
    /// </summary>
    public Guid? SetByUserId { get; set; }

    /// <summary>When BarrioSetupStartDate was last modified.</summary>
    public Instant? SetAt { get; set; }
}
