using Humans.Base.Attributes;
using NodaTime;

using Humans.Shifts.Contracts;
namespace Humans.Shifts.Domain;

/// <summary>
/// A shift container belonging to a department (parent team) and event.
/// Groups related shifts under a named rota with shared priority and signup policy.
/// </summary>
internal sealed class Rota
{
    public Guid Id { get; init; }

    public Guid EventSettingsId { get; set; }

    public Guid TeamId { get; set; }

    public string Name { get; set; } = string.Empty;

    [MarkdownContent]
    public string? Description { get; set; }

    public ShiftPriority Priority { get; set; }

    public SignupPolicy Policy { get; set; }

    /// <summary>
    /// Explicit period set by the coordinator. Drives creation UX (all-day vs time-slotted)
    /// and signup UX (date-range vs individual).
    /// </summary>
    public RotaPeriod Period { get; set; } = RotaPeriod.Event;

    /// <summary>
    /// Meeting point, pre-shift instructions, what to bring. Shared by all shifts in the rota.
    /// </summary>
    [MarkdownContent]
    [System.ComponentModel.DataAnnotations.MaxLength(2000)]
    public string? PracticalInfo { get; set; }

    /// <summary>
    /// Whether this rota is visible to volunteers on the browse page.
    /// Coordinators can disable to stage rollout of signup.
    /// </summary>
    public bool IsVisibleToVolunteers { get; set; } = true;

    public Instant CreatedAt { get; init; }

    public Instant UpdatedAt { get; set; }

    public EventSettings EventSettings { get; set; } = null!;

    public ICollection<Shift> Shifts { get; } = new List<Shift>();

    public ICollection<ShiftTag> Tags { get; } = new List<ShiftTag>();
}
