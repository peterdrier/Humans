using Humans.Domain.Entities;
using Humans.Domain.Enums;

namespace Humans.Application.Interfaces;

/// <summary>
/// Service owning user-level concerns. Currently focused on event participation.
/// </summary>
public interface IUserService
{
    /// <summary>
    /// Get the participation record for a user in a given year. Returns null if no record exists.
    /// </summary>
    Task<EventParticipation?> GetParticipationAsync(Guid userId, int year);

    /// <summary>
    /// Get all participation records for a given year.
    /// </summary>
    Task<List<EventParticipation>> GetAllParticipationsForYearAsync(int year);

    /// <summary>
    /// Declare that the user is not attending this year's event.
    /// </summary>
    Task<EventParticipation> DeclareNotAttendingAsync(Guid userId, int year);

    /// <summary>
    /// Undo a "not attending" declaration. Removes the record.
    /// Only works if the current status is NotAttending with Source=UserDeclared.
    /// </summary>
    Task<bool> UndoNotAttendingAsync(Guid userId, int year);

    /// <summary>
    /// Set participation status from ticket sync. Handles the lifecycle rules:
    /// - Valid ticket → Ticketed
    /// - Checked in → Attended (permanent)
    /// - Ticket purchase overrides NotAttending
    /// </summary>
    Task SetParticipationFromTicketSyncAsync(Guid userId, int year, ParticipationStatus status);

    /// <summary>
    /// Remove a TicketSync-sourced participation record when a user no longer has valid tickets.
    /// Does not remove UserDeclared or AdminBackfill records.
    /// Does not remove Attended records (permanent).
    /// </summary>
    Task RemoveTicketSyncParticipationAsync(Guid userId, int year);

    /// <summary>
    /// Bulk import historical participation data (admin backfill).
    /// </summary>
    Task<int> BackfillParticipationsAsync(int year, List<(Guid UserId, ParticipationStatus Status)> entries);
}
