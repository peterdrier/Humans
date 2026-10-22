using Humans.Base.Interfaces;
using NodaTime;

namespace Humans.Tickets.Contracts;

/// <summary>Gate's door admission. Every other participation write stays inside Tickets.</summary>
public interface ITicketWrite : IApplicationService
{
    /// <summary>
    /// Attended is permanent (only a missing <paramref name="checkedInAt"/> may still be filled), a ticket overrides a
    /// NotAttending declaration, and the first non-null <paramref name="checkedInAt"/> is kept.
    /// </summary>
    Task RecordParticipationAsync(
        Guid userId,
        int year,
        ParticipationStatus status,
        Instant? checkedInAt,
        CancellationToken ct = default);
}
