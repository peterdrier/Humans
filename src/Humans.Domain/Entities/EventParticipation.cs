using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Domain.Entities;

/// <summary>
/// Per-user, per-year record tracking event participation status.
/// No record = unknown/no response yet (default state, not stored).
/// </summary>
public class EventParticipation
{
    public Guid Id { get; init; }

    /// <summary>
    /// The user this participation record belongs to.
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// The event year (e.g. 2026).
    /// </summary>
    public int Year { get; set; }

    /// <summary>
    /// Current participation status.
    /// </summary>
    public ParticipationStatus Status { get; set; }

    /// <summary>
    /// When the user self-declared NotAttending (null for other sources).
    /// </summary>
    public Instant? DeclaredAt { get; set; }

    /// <summary>
    /// How the status was set.
    /// </summary>
    public ParticipationSource Source { get; set; }

    /// <summary>
    /// Navigation property to the user.
    /// </summary>
    [Obsolete("Cross-domain nav; resolve via IUserService.GetByIdAsync(UserId) instead. See design-rules §6c.")]
    public User User { get; set; } = null!;
}
