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
    /// Returns all emails for a user as tracked entities for modification.
    /// </summary>
    Task<IReadOnlyList<UserEmail>> GetByUserIdTrackedAsync(
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
    /// Batch lookup of every <see cref="UserEmail"/> row (any verification
    /// state) for the given user ids. Returned read-only. Callers group by
    /// <see cref="UserEmail.UserId"/> as needed.
    /// </summary>
    Task<IReadOnlyList<UserEmail>> GetByUserIdsAsync(
        IEnumerable<Guid> userIds, CancellationToken ct = default);

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
    /// Persists changes to a single tracked <see cref="UserEmail"/> entity.
    /// The caller must have obtained the entity via a tracked query method
    /// in the same scope.
    /// </summary>
    Task UpdateAsync(UserEmail email, CancellationToken ct = default);

    /// <summary>
    /// Persists changes to multiple tracked <see cref="UserEmail"/> entities
    /// in one SaveChanges call. Used by batch operations (e.g., setting
    /// notification target across all emails for a user).
    /// </summary>
    Task UpdateBatchAsync(IReadOnlyList<UserEmail> emails, CancellationToken ct = default);
}
