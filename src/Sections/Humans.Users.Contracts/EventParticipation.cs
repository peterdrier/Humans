using NodaTime;

namespace Humans.Users.Contracts;

/// <summary>
/// The legacy <c>event_participations</c> row: frozen since Users decomposition commit 13, when
/// participation moved to Tickets' <c>tickets_event_participations</c>. Nothing writes it; since the
/// <c>tickets.event-participations</c> copy was retired it is read only by Users' export of the rows
/// it still holds, until USERS-19 drops the table.
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
    public LegacyParticipationStatus Status { get; set; }

    /// <summary>
    /// When the user self-declared NotAttending (null for other sources).
    /// </summary>
    public Instant? DeclaredAt { get; set; }

    /// <summary>
    /// When the user was first checked in at the event gate (from the
    /// TicketTailor <c>check_in.checked_in_at</c> field or a local gate admit).
    /// Populated by a scan establishing <see cref="LegacyParticipationStatus.Attended"/>,
    /// including a later scan when attendance was already known. Once set it is never
    /// cleared or overwritten — matches the existing invariant that
    /// <see cref="LegacyParticipationStatus.Attended"/> rows are permanent and not
    /// removed by sync. May be null if the vendor did not return a timestamp
    /// when the status flipped (graceful fallback).
    /// </summary>
    public Instant? CheckedInAt { get; set; }

    /// <summary>
    /// How the status was set.
    /// </summary>
    public LegacyParticipationSource Source { get; set; }
}
