using Humans.Application.DTOs;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;

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
    /// Returns every user email, read-only. Used by the duplicate-account
    /// scan to detect overlapping addresses across users. Trivial to load in
    /// full at ~500-user scale.
    /// </summary>
    Task<IReadOnlyList<UserEmail>> GetAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Removes every <see cref="UserEmail"/> row for the given user. Used
    /// during account merge/duplicate-resolve to wipe the source's addresses
    /// before anonymization.
    /// </summary>
    Task RemoveAllForUserAndSaveAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Marks a single email as verified and bumps <see cref="UserEmail.UpdatedAt"/>
    /// to <paramref name="now"/>. Returns false if the email does not exist.
    /// Used by <c>AccountMergeService.AcceptAsync</c> to complete a merge.
    /// </summary>
    Task<bool> MarkVerifiedAsync(
        Guid emailId, NodaTime.Instant now, CancellationToken ct = default);

    /// <summary>
    /// Removes a single email by id. Returns false if the email does not
    /// exist. Used by <c>AccountMergeService.RejectAsync</c> to clear the
    /// pending unverified address on rejection.
    /// </summary>
    Task<bool> RemoveByIdAsync(Guid emailId, CancellationToken ct = default);

    /// <summary>
    /// Returns every <see cref="UserEmail"/> row whose <c>Email</c> matches one
    /// of <paramref name="emails"/> (case-insensitive). Read-only (AsNoTracking).
    /// Used by the Google admin workspace-accounts list to match Google-side
    /// accounts to human records without loading the full table.
    /// </summary>
    Task<IReadOnlyList<UserEmail>> GetByEmailsAsync(
        IReadOnlyCollection<string> emails, CancellationToken ct = default);

    /// <summary>
    /// Returns true when any <see cref="UserEmail"/> row exists with
    /// <c>Email</c> equal to <paramref name="email"/> (case-insensitive),
    /// irrespective of user. Used by admin account-linking flows.
    /// </summary>
    Task<bool> AnyWithEmailAsync(string email, CancellationToken ct = default);

    /// <summary>
    /// Returns a mapping of userId → verified notification-target email for all users
    /// that have one. If a user has multiple verified notification-target emails,
    /// one is picked arbitrarily.
    /// </summary>
    Task<Dictionary<Guid, string>> GetAllNotificationTargetEmailsAsync(
        CancellationToken ct = default);

    /// <summary>
    /// Returns distinct user ids whose verified email set contains the given
    /// case-insensitive substring. Used by admin search surfaces (Tickets
    /// "who hasn't bought") so secondary verified addresses are discoverable
    /// even when they differ from the notification-target email.
    /// </summary>
    Task<IReadOnlyList<Guid>> SearchUserIdsByVerifiedEmailAsync(
        string searchTerm, CancellationToken ct = default);

    /// <summary>
    /// Finds a verified UserEmail matching the normalized (or alternate) address,
    /// returning minimal User info for conflict checking.
    /// </summary>
    Task<UserEmailWithUser?> FindVerifiedWithUserAsync(
        string normalizedEmail, string? alternateEmail,
        CancellationToken ct = default);

    /// <summary>
    /// Finds any <see cref="UserEmail"/> (verified or unverified, OAuth or not)
    /// whose address matches the given normalized email — or its
    /// googlemail/gmail alternate — using case-insensitive comparison.
    /// Used by account provisioning to dedupe incoming contacts against
    /// every known email for every user.
    /// </summary>
    Task<UserEmail?> FindByNormalizedEmailAsync(
        string normalizedEmail, string? alternateEmail,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the email address for a verified email owned by the user,
    /// or null if not found or not verified.
    /// </summary>
    Task<string?> GetVerifiedEmailAddressAsync(
        Guid userId, Guid emailId, CancellationToken ct = default);

    /// <summary>
    /// Returns the owning <c>UserId</c> for a verified email address matching
    /// the given string exactly (no gmail/googlemail aliasing). Returns
    /// <c>null</c> if no verified row matches.
    /// </summary>
    Task<Guid?> GetUserIdByVerifiedEmailAsync(
        string email, CancellationToken ct = default);

    /// <summary>
    /// Returns the id of any user, other than <paramref name="excludeUserId"/>,
    /// whose <c>user_emails</c> rows contain the given address (case-insensitive).
    /// Used by @nobodies.team provisioning to block a prefix that is already
    /// attached to another human regardless of verification state.
    /// </summary>
    Task<Guid?> GetOtherUserIdHavingEmailAsync(
        string email, Guid excludeUserId, CancellationToken ct = default);

    /// <summary>
    /// Rewrites the <c>Email</c> of the user's OAuth-sourced <see cref="UserEmail"/>
    /// row (if one exists) to <paramref name="newEmail"/>. Used by the admin
    /// email-backfill workflow. Returns true when a row was updated. No-op if the
    /// user has no OAuth email row.
    /// </summary>
    Task<bool> RewriteOAuthEmailAsync(Guid userId, string newEmail, CancellationToken ct = default);

    /// <summary>
    /// Rewrites the <c>Email</c> of the user's existing <see cref="UserEmail"/> row
    /// (case-insensitive match on <paramref name="oldEmail"/>) to
    /// <paramref name="newEmail"/> and stamps <c>UpdatedAt</c> with
    /// <paramref name="updatedAt"/>. Used by the admin rename-fix flow. Returns
    /// true when a row was updated. No-op if no matching row exists.
    /// </summary>
    Task<bool> RewriteEmailAddressAsync(
        Guid userId, string oldEmail, string newEmail, Instant updatedAt,
        CancellationToken ct = default);

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
