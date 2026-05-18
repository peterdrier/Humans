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
    /// When the user was first checked in at the event gate (from the
    /// TicketTailor <c>check_in.checked_in_at</c> field). Populated only when
    /// <see cref="Status"/> becomes <see cref="ParticipationStatus.Attended"/>
    /// via <see cref="ParticipationSource.TicketSync"/>. Once set it is never
    /// cleared or overwritten — matches the existing invariant that
    /// <see cref="ParticipationStatus.Attended"/> rows are permanent and not
    /// removed by sync. May be null if the vendor did not return a timestamp
    /// when the status flipped (graceful fallback).
    /// </summary>
    public Instant? CheckedInAt { get; set; }

    /// <summary>
    /// How the status was set.
    /// </summary>
    public ParticipationSource Source { get; set; }

    /// <summary>
    /// Navigation property to the user.
    /// </summary>
    public User User { get; set; } = null!;
}
