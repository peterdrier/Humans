using Humans.Base.Interfaces;
using NodaTime;

namespace Humans.Tickets.Contracts;

/// <summary>
/// The participation write another section makes: Gate admits a guest at the door. Every other
/// participation write (declare / undo not attending, ticket-sync reconciliation, admin backfill)
/// happens inside Tickets.
/// </summary>
public interface ITicketWrite : IApplicationService
{
    /// <summary>
    /// Set participation from a ticket source. Lifecycle rules: Attended is permanent (only a
    /// missing <paramref name="checkedInAt"/> may still be filled), a ticket overrides a
    /// NotAttending declaration, and the first non-null <paramref name="checkedInAt"/> is kept.
    /// </summary>
    Task RecordParticipationAsync(
        Guid userId,
        int year,
        ParticipationStatus status,
        Instant? checkedInAt,
        CancellationToken ct = default);
}
