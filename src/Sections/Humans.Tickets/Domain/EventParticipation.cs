using Humans.Tickets.Contracts;
using NodaTime;

namespace Humans.Tickets.Domain;

/// <summary>No row means unknown.</summary>
internal sealed class EventParticipation
{
    public Guid Id { get; init; }

    public Guid UserId { get; set; }

    public int Year { get; set; }

    /// <summary>
    /// Null marks a cleared row: kept only while a legacy Users row exists for the same user and year, so that value
    /// can't come back through the read fallback or the copy. Reads treat it as no record.
    /// </summary>
    public ParticipationStatus? Status { get; set; }

    public Instant? DeclaredAt { get; set; }

    /// <summary>First gate arrival; never cleared or overwritten once set. May stay null when no timestamp came.</summary>
    public Instant? CheckedInAt { get; set; }

    public ParticipationSource Source { get; set; }
}
