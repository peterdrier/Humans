using Humans.Base.Attributes;
using NodaTime;

using Humans.Shifts.Contracts;
namespace Humans.Shifts.Domain;

/// <summary>Belongs to a department (parent team) and an event.</summary>
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

    /// <summary>Drives creation (all-day vs time-slotted) and signup (date range vs individual).</summary>
    public RotaPeriod Period { get; set; } = RotaPeriod.Event;

    /// <summary>Meeting point, instructions, what to bring; shared by all the rota's shifts.</summary>
    [MarkdownContent]
    [System.ComponentModel.DataAnnotations.MaxLength(2000)]
    public string? PracticalInfo { get; set; }

    /// <summary>Coordinators clear it to stage a signup rollout.</summary>
    public bool IsVisibleToVolunteers { get; set; } = true;

    public Instant CreatedAt { get; init; }

    public Instant UpdatedAt { get; set; }

    public EventSettings EventSettings { get; set; } = null!;

    public ICollection<Shift> Shifts { get; } = new List<Shift>();

    public ICollection<ShiftTag> Tags { get; } = new List<ShiftTag>();
}
