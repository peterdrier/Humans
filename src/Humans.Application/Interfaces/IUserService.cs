using Humans.Domain.Entities;
using Humans.Domain.Enums;

namespace Humans.Application.Interfaces;

/// <summary>
/// Service owning user-level concerns. Currently focused on event participation.
/// </summary>
public interface IUserService
{
    /// <summary>
    /// Fetches a single user by id. Returns null if the user does not exist.
    /// Used by section services that need a slice of user data (email,
    /// display name, preferred language) for rendering or notifications
    /// without loading a cross-domain navigation property.
    /// </summary>
    Task<User?> GetByIdAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Fetches a batched set of users keyed by id. Missing users are simply
    /// absent from the returned dictionary. Used for in-memory stitching
    /// when rendering lists that previously relied on
    /// <c>.Include(x =&gt; x.User)</c>.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, User>> GetByIdsAsync(
        IReadOnlyCollection<Guid> userIds,
        CancellationToken ct = default);

    /// <summary>
    /// Get the participation record for a user in a given year. Returns null if no record exists.
    /// </summary>
    Task<EventParticipation?> GetParticipationAsync(Guid userId, int year, CancellationToken ct = default);

    /// <summary>
    /// Get all participation records for a given year.
    /// </summary>
    Task<List<EventParticipation>> GetAllParticipationsForYearAsync(int year, CancellationToken ct = default);

    /// <summary>
    /// Declare that the user is not attending this year's event.
    /// </summary>
    Task<EventParticipation> DeclareNotAttendingAsync(Guid userId, int year, CancellationToken ct = default);

    /// <summary>
    /// Undo a "not attending" declaration. Removes the record.
    /// Only works if the current status is NotAttending with Source=UserDeclared.
    /// </summary>
    Task<bool> UndoNotAttendingAsync(Guid userId, int year, CancellationToken ct = default);

    /// <summary>
    /// Set participation status from ticket sync. Handles the lifecycle rules:
    /// - Valid ticket → Ticketed
    /// - Checked in → Attended (permanent)
    /// - Ticket purchase overrides NotAttending
    /// </summary>
    Task SetParticipationFromTicketSyncAsync(Guid userId, int year, ParticipationStatus status, CancellationToken ct = default);

    /// <summary>
    /// Remove a TicketSync-sourced participation record when a user no longer has valid tickets.
    /// Does not remove UserDeclared or AdminBackfill records.
    /// Does not remove Attended records (permanent).
    /// </summary>
    Task RemoveTicketSyncParticipationAsync(Guid userId, int year, CancellationToken ct = default);

    /// <summary>
    /// Bulk import historical participation data (admin backfill).
    /// </summary>
    Task<int> BackfillParticipationsAsync(int year, List<(Guid UserId, ParticipationStatus Status)> entries, CancellationToken ct = default);

    // ---- Methods added for Profile-section migration (§15 Step 0) ----

    /// <summary>
    /// Sets <c>User.GoogleEmail</c> if it is currently null. No-op if the
    /// user already has a GoogleEmail set or the user does not exist.
    /// Returns true if the GoogleEmail was set.
    /// </summary>
    Task<bool> TrySetGoogleEmailAsync(Guid userId, string email, CancellationToken ct = default);

    /// <summary>
    /// Updates <c>User.DisplayName</c>. No-op if the user does not exist.
    /// </summary>
    Task UpdateDisplayNameAsync(Guid userId, string displayName, CancellationToken ct = default);
}
