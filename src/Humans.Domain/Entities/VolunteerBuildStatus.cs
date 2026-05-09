using NodaTime;

namespace Humans.Domain.Entities;

/// <summary>
/// Per-user, per-event Shifts-owned coordination state used by the Volunteer
/// Tracking page: optional camp-set-up start date.
///
/// One row per (UserId, EventSettingsId). A row with BarrioSetupStartDate=null
/// is functionally equivalent to no row.
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
    /// Optional free-text from the coordinator who set/cleared the camp
    /// set-up date.
    /// </summary>
    public string? Notes { get; set; }

    /// <summary>
    /// Coordinator who last modified BarrioSetupStartDate.
    /// </summary>
    public Guid? SetByUserId { get; set; }

    /// <summary>When BarrioSetupStartDate was last modified.</summary>
    public Instant? SetAt { get; set; }
}
