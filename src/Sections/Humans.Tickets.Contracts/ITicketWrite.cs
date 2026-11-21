using Humans.Base.Interfaces;
using NodaTime;

namespace Humans.Tickets.Contracts;

/// <summary>Gate's door admission and its undo. Every other participation write stays inside Tickets.</summary>
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

    /// <summary>
    /// The gate undid the member's only admit for <paramref name="year"/>: an Attended record goes back to Ticketed with
    /// no arrival, so a later admit records a fresh one. Anything else is left alone. False when nothing changed.
    /// </summary>
    Task<bool> UndoCheckInAsync(Guid userId, int year, CancellationToken ct = default);
}
