using Humans.Base.Interfaces;
using Humans.Tickets.Contracts;

namespace Humans.Tickets.Services;

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

    /// <summary>Legacy Users rows with no <c>tickets_event_participations</c> row (cleared or not) for
    /// their user and year, oldest year first.</summary>
    Task<IReadOnlyList<EventParticipationInfo>> GetUncopiedLegacyParticipationsAsync(CancellationToken ct = default);

    /// <summary>Copies every pending legacy row. Idempotent: a row written since the list was read is
    /// left alone. Returns the rows written.</summary>
    Task<int> CopyLegacyParticipationsAsync(CancellationToken ct = default);
}
