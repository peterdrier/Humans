using Humans.Base.Interfaces;
using Humans.Tickets.Contracts;

namespace Humans.Tickets.Services;

/// <summary>
/// Tickets-internal surface over <c>tickets_event_participations</c>: the reads <see cref="ITicketService"/>
/// forwards, the member's own declaration, ticket-sync reconciliation and the admin backfill.
/// </summary>
internal interface IEventParticipationManagement : ITicketWrite, IApplicationService
{
    /// <inheritdoc cref="ITicketService.GetParticipationsAsync"/>
    Task<IReadOnlyList<EventParticipationInfo>> GetParticipationsAsync(Guid userId, CancellationToken ct = default);

    /// <inheritdoc cref="ITicketService.GetAllParticipationsAsync"/>
    Task<IReadOnlyList<EventParticipationInfo>> GetAllParticipationsAsync(CancellationToken ct = default);

    /// <summary>The member declares they are not attending <paramref name="year"/>. Refused (false) once
    /// they are Attended; a ticket held later overrides the declaration.</summary>
    Task<bool> DeclareNotAttendingAsync(Guid userId, int year, CancellationToken ct = default);

    /// <summary>Undo the member's own NotAttending declaration. Only a UserDeclared NotAttending record
    /// is removed; returns false otherwise.</summary>
    Task<bool> UndoNotAttendingAsync(Guid userId, int year, CancellationToken ct = default);

    /// <summary>Remove a TicketSync Ticketed record once the member holds no valid ticket.</summary>
    Task RemoveTicketSyncParticipationAsync(Guid userId, int year, CancellationToken ct = default);

    /// <summary>Bulk import of historical participation after admin confirmation; each changed record is
    /// audited as <c>ParticipationBackfilled</c> with the admin as actor. Returns the count changed.</summary>
    Task<int> BackfillParticipationsAsync(
        int year, IReadOnlyList<(Guid UserId, ParticipationStatus Status)> entries, Guid actorUserId,
        CancellationToken ct = default);

}
