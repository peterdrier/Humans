using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Domain.Entities;

/// <summary>
/// A shift container belonging to a department (parent team) and event.
/// Groups related shifts under a named rota with shared priority and signup policy.
/// </summary>
public class Rota
{
    /// <summary>
    /// Unique identifier.
    /// </summary>
    public Guid Id { get; init; }

    /// <summary>
    /// FK to the event configuration this rota belongs to.
    /// </summary>
    public Guid EventSettingsId { get; set; }

    /// <summary>
    /// FK to the department (parent team) this rota belongs to.
    /// </summary>
    public Guid TeamId { get; set; }

    /// <summary>
    /// Display name for the rota (e.g., "Gate Shifts", "Bar Cleanup").
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Optional description of what this rota covers.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Priority level affecting urgency scoring.
    /// </summary>
    public ShiftPriority Priority { get; set; }

    /// <summary>
    /// Whether signups are auto-confirmed or require coordinator approval.
    /// </summary>
    public SignupPolicy Policy { get; set; }

    /// <summary>
    /// Explicit period set by the coordinator. Drives creation UX (all-day vs time-slotted)
    /// and signup UX (date-range vs individual).
    /// </summary>
    public RotaPeriod Period { get; set; } = RotaPeriod.Event;

    /// <summary>
    /// Meeting point, pre-shift instructions, what to bring. Shared by all shifts in the rota.
    /// </summary>
    [System.ComponentModel.DataAnnotations.MaxLength(2000)]
    public string? PracticalInfo { get; set; }

    /// <summary>
    /// Whether this rota is visible to volunteers on the browse page.
    /// Coordinators can disable to stage rollout of signup.
    /// </summary>
    public bool IsVisibleToVolunteers { get; set; } = true;

    /// <summary>
    /// When this rota was created.
    /// </summary>
    public Instant CreatedAt { get; init; }

    /// <summary>
    /// When this rota was last updated.
    /// </summary>
    public Instant UpdatedAt { get; set; }

    /// <summary>
    /// Navigation property to the event configuration.
    /// </summary>
    public EventSettings EventSettings { get; set; } = null!;

    /// <summary>
    /// Navigation property to the department (parent team).
    /// </summary>
    public Team Team { get; set; } = null!;

    /// <summary>
    /// Navigation property to shifts within this rota.
    /// </summary>
    public ICollection<Shift> Shifts { get; } = new List<Shift>();
}
