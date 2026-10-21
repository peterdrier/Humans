using Humans.Tickets.Contracts;
using NodaTime;

namespace Humans.Tickets.Domain;

/// <summary>
/// One member's participation in one event year (<c>tickets_event_participations</c>, one row per
/// user and year). No row = unknown / no response yet.
/// </summary>
internal sealed class EventParticipation
{
    public Guid Id { get; init; }

    /// <summary>The member. Bare id, no FK to <c>users</c>.</summary>
    public Guid UserId { get; set; }

    /// <summary>The event year (e.g. 2026).</summary>
    public int Year { get; set; }

    /// <summary>
    /// Current status. Null marks a <b>cleared</b> row: the record was removed (undo, stale ticket,
    /// merge fold) while the frozen legacy Users row for the same user and year still exists, so the
    /// row stays to keep that legacy value from reappearing through the read fallback or the
    /// <c>tickets.event-participations</c> copy. Reads treat a cleared row as no record. Only written
    /// until the copy's retirement PR.
    /// </summary>
    public ParticipationStatus? Status { get; set; }

    /// <summary>When the member self-declared NotAttending (null for other sources).</summary>
    public Instant? DeclaredAt { get; set; }

    /// <summary>
    /// First gate arrival (vendor <c>check_in.checked_in_at</c> or a local gate admit). Set by a scan
    /// establishing <see cref="ParticipationStatus.Attended"/>, including a later scan when attendance
    /// was already known; never cleared or overwritten once set. May stay null when no timestamp came.
    /// </summary>
    public Instant? CheckedInAt { get; set; }

    /// <summary>How the status was set.</summary>
    public ParticipationSource Source { get; set; }
}
