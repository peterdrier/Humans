using NodaTime;

namespace Humans.Users.Contracts;

/// <summary>
/// The frozen legacy <c>event_participations</c> row: nothing writes it. Read only for Tickets' legacy fallback,
/// its copy, and Users' export. The table goes once production reports the copy done.
/// </summary>
public class EventParticipation
{
    public Guid Id { get; init; }

    public Guid UserId { get; set; }

    public int Year { get; set; }

    public LegacyParticipationStatus Status { get; set; }

    /// <summary>When the user self-declared NotAttending; null for other sources.</summary>
    public Instant? DeclaredAt { get; set; }

    /// <summary>
    /// First gate check-in. Once set it is never cleared or overwritten. Null if the vendor returned no timestamp.
    /// </summary>
    public Instant? CheckedInAt { get; set; }

    public LegacyParticipationSource Source { get; set; }
}
