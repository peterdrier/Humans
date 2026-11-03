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
    /// Historical cleared rows have null status and read as unknown. New removals delete the row.
    /// </summary>
    public ParticipationStatus? Status { get; set; }

    public Instant? DeclaredAt { get; set; }

    /// <summary>First gate arrival; never cleared or overwritten once set. May stay null when no timestamp came.</summary>
    public Instant? CheckedInAt { get; set; }

    public ParticipationSource Source { get; set; }
}
