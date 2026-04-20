using Humans.Application.DTOs;
using Humans.Domain.Entities;
using Humans.Domain.Enums;

namespace Humans.Application.Interfaces.Repositories;

/// <summary>
/// Repository for the <c>user_emails</c> table.
/// The only non-test file that may write to this DbSet.
/// </summary>
public interface IUserEmailRepository
{
    /// <summary>
    /// Returns all emails for a user, read-only, ordered by
    /// <c>DisplayOrder</c> then <c>CreatedAt</c>.
    /// </summary>
    Task<IReadOnlyList<UserEmail>> GetByUserIdReadOnlyAsync(
        Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Returns detached entities intended to be mutated in-memory and passed back
    /// to <see cref="UpdateAsync"/> or <see cref="UpdateBatchAsync"/>. The returned
    /// entities are NOT tracked — callers must explicitly hand mutated entities
    /// back to a write method for persistence.
    /// </summary>
    Task<IReadOnlyList<UserEmail>> GetByUserIdForMutationAsync(
        Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Returns a single email by id and user id, tracked for modification.
    /// </summary>
    Task<UserEmail?> GetByIdAndUserIdAsync(
        Guid emailId, Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Returns a single email by id, read-only.
    /// </summary>
    Task<UserEmail?> GetByIdReadOnlyAsync(Guid emailId, CancellationToken ct = default);

    /// <summary>
    /// Checks whether an email (or gmail/googlemail alternate) already
    /// exists for this user.
    /// </summary>
    Task<bool> ExistsForUserAsync(
        Guid userId, string normalizedEmail, string? alternateEmail,
        CancellationToken ct = default);

    /// <summary>
    /// Checks whether a verified email (or gmail/googlemail alternate) exists
    /// for a different user. Used for conflict/merge detection.
    /// </summary>
    Task<bool> ExistsVerifiedForOtherUserAsync(
        Guid userId, string normalizedEmail, string? alternateEmail,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the first verified email matching the normalized (or alternate)
    /// address that belongs to a different user. For merge flow.
    /// </summary>
    Task<UserEmail?> GetConflictingVerifiedEmailAsync(
        Guid excludeEmailId, string normalizedEmail, string? alternateEmail,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the maximum display order for a user's emails.
    /// </summary>
    Task<int> GetMaxDisplayOrderAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Returns all verified @nobodies.team emails across all users.
    /// Used as a bulk load to support in-memory filtering by callers.
    /// </summary>
    Task<IReadOnlyList<UserEmail>> GetAllVerifiedNobodiesTeamEmailsAsync(
        CancellationToken ct = default);

    /// <summary>
    /// Returns a mapping of userId → verified notification-target email for all users
    /// that have one. If a user has multiple verified notification-target emails,
    /// one is picked arbitrarily.
    /// </summary>
    Task<Dictionary<Guid, string>> GetAllNotificationTargetEmailsAsync(
        CancellationToken ct = default);

    /// <summary>
    /// Finds a verified UserEmail matching the normalized (or alternate) address,
    /// returning minimal User info for conflict checking.
    /// </summary>
    Task<UserEmailWithUser?> FindVerifiedWithUserAsync(
        string normalizedEmail, string? alternateEmail,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the email address for a verified email owned by the user,
    /// or null if not found or not verified.
    /// </summary>
    Task<string?> GetVerifiedEmailAddressAsync(
        Guid userId, Guid emailId, CancellationToken ct = default);

    Task AddAsync(UserEmail email, CancellationToken ct = default);
    Task RemoveAsync(UserEmail email, CancellationToken ct = default);
    Task RemoveAllForUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Persists changes to a single <see cref="UserEmail"/> entity by attaching it
    /// to a fresh context and marking it as Modified.
    /// </summary>
    Task UpdateAsync(UserEmail email, CancellationToken ct = default);

    /// <summary>
    /// Persists changes to multiple <see cref="UserEmail"/> entities in one
    /// SaveChanges call. Each entity is attached and marked as Modified.
    /// </summary>
    Task UpdateBatchAsync(IReadOnlyList<UserEmail> emails, CancellationToken ct = default);
}
