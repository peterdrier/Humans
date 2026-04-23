using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.Interfaces.Repositories;

/// <summary>
/// Repository for the <c>AspNetUsers</c> (via <see cref="User"/>) and
/// <c>event_participations</c> tables. The only non-test file that may write
/// to those DbSets after the User migration lands.
/// </summary>
/// <remarks>
/// Read methods are <c>AsNoTracking</c>. Narrow-field updates commit atomically
/// in a single <see cref="Microsoft.EntityFrameworkCore.IDbContextFactory{HumansDbContext}"/>-owned
/// context. Event-participation mutations expose load-then-save primitives so
/// <see cref="Humans.Application.Services.Users.UserService"/> can apply the
/// status/source business rules before persisting.
/// </remarks>
public interface IUserRepository
{
    // ==========================================================================
    // Reads — User
    // ==========================================================================

    /// <summary>
    /// Loads a single user by id. Read-only (AsNoTracking). Returns null if
    /// the user does not exist.
    /// </summary>
    Task<User?> GetByIdAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Batched user fetch keyed by id. Missing users are absent from the
    /// returned dictionary. Read-only (AsNoTracking).
    /// </summary>
    Task<IReadOnlyDictionary<Guid, User>> GetByIdsAsync(
        IReadOnlyCollection<Guid> userIds,
        CancellationToken ct = default);

    /// <summary>
    /// Loads every user, read-only (AsNoTracking). Used by admin list views
    /// that must include profileless users. Trivial at ~500-user scale.
    /// </summary>
    Task<IReadOnlyList<User>> GetAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the ids of every user in the system, read-only. Used by the
    /// admin dashboard to partition all users into status buckets without
    /// loading the full User graph.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetAllUserIdsAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the language distribution of the given user ids, grouped by
    /// <see cref="User.PreferredLanguage"/>. Used by the admin dashboard to
    /// render language stats for approved humans after the caller has
    /// resolved the approved user id set from the Profile section.
    /// </summary>
    Task<IReadOnlyList<(string Language, int Count)>>
        GetLanguageDistributionForUserIdsAsync(
            IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);

    /// <summary>
    /// Finds a user whose <c>Email</c> or <c>GoogleEmail</c> matches the given
    /// normalized address (case-insensitive). If <paramref name="alternateEmail"/>
    /// is non-null, also matches users whose email matches the alternate form
    /// (gmail.com ↔ googlemail.com). Read-only.
    /// </summary>
    Task<User?> GetByEmailOrAlternateAsync(
        string normalizedEmail, string? alternateEmail, CancellationToken ct = default);

    /// <summary>
    /// Finds a user whose <c>NormalizedEmail</c> exactly matches the supplied
    /// value. The caller is responsible for normalizing the input (Identity's
    /// <c>UserManager.NormalizeEmail</c>). Read-only (AsNoTracking). Used by
    /// <c>MagicLinkService.FindUserByAnyEmailAsync</c> as the fallback lookup
    /// when no verified UserEmail row exists.
    /// </summary>
    Task<User?> GetByNormalizedEmailAsync(
        string? normalizedEmail, CancellationToken ct = default);

    /// <summary>
    /// Returns all contact users (ContactSource != null, LastLoginAt == null),
    /// optionally filtered by display name or email substring.
    /// Ordered by CreatedAt descending. Read-only.
    /// </summary>
    Task<IReadOnlyList<User>> GetContactUsersAsync(string? search, CancellationToken ct = default);

    /// <summary>
    /// Returns the <c>LastLoginAt</c> timestamp of every user whose last login
    /// falls within the half-open window <c>[fromInclusive, toExclusive)</c>.
    /// Read-only (AsNoTracking). Used by the shift coordinator dashboard.
    /// </summary>
    Task<IReadOnlyList<Instant>> GetLoginTimestampsInWindowAsync(
        Instant fromInclusive, Instant toExclusive, CancellationToken ct = default);

    /// <summary>
    /// Returns the id of any user, other than <paramref name="excludeUserId"/>,
    /// whose <c>GoogleEmail</c> matches the given address (case-insensitive),
    /// or null if no such user exists. Used by @nobodies.team provisioning to
    /// block a prefix that is already attached to another human.
    /// </summary>
    Task<Guid?> GetOtherUserIdHavingGoogleEmailAsync(
        string email, Guid excludeUserId, CancellationToken ct = default);

    // ==========================================================================
    // Writes — User (atomic field updates)
    // ==========================================================================

    /// <summary>
    /// Updates <c>User.DisplayName</c>. Returns false if the user does not exist.
    /// </summary>
    Task<bool> UpdateDisplayNameAsync(Guid userId, string displayName, CancellationToken ct = default);

    /// <summary>
    /// Sets <c>User.GoogleEmail</c> if and only if it is currently null.
    /// No-op if the user already has a GoogleEmail set or the user does not
    /// exist. Returns true if the GoogleEmail was set.
    /// </summary>
    Task<bool> TrySetGoogleEmailAsync(Guid userId, string email, CancellationToken ct = default);

    /// <summary>
    /// Unconditionally sets <c>User.GoogleEmail</c>, overwriting any existing
    /// value. Used by the Workspace provisioning path after a successful
    /// Google account creation. Returns true if the user exists and the
    /// value was written, false if the user does not exist.
    /// </summary>
    Task<bool> SetGoogleEmailAsync(Guid userId, string email, CancellationToken ct = default);

    /// <summary>
    /// Rewrites <c>User.Email</c>, <c>User.UserName</c>, <c>User.NormalizedEmail</c>,
    /// and <c>User.NormalizedUserName</c> to the given <paramref name="newEmail"/>.
    /// Used by the admin email-backfill workflow to repair OAuth identity after a
    /// provider-side email change. Returns the previous <c>Email</c> value (may be
    /// null) so callers can log the transition, or <c>(false, null)</c> if the user
    /// does not exist.
    /// </summary>
    Task<(bool Updated, string? OldEmail)> RewritePrimaryEmailAsync(
        Guid userId, string newEmail, CancellationToken ct = default);

    /// <summary>
    /// Sets the deletion-pending fields on a user (<c>DeletionRequestedAt</c>,
    /// <c>DeletionScheduledFor</c>). Returns false if the user does not exist.
    /// </summary>
    Task<bool> SetDeletionPendingAsync(
        Guid userId, Instant requestedAt, Instant scheduledFor, CancellationToken ct = default);

    /// <summary>
    /// Clears deletion-pending fields (<c>DeletionRequestedAt</c>,
    /// <c>DeletionScheduledFor</c>, <c>DeletionEligibleAfter</c>).
    /// Returns false if the user does not exist.
    /// </summary>
    Task<bool> ClearDeletionAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Anonymizes the identity portion of a user record for the account-merge
    /// / duplicate-resolve flow: display name, username, email fields, phone,
    /// profile picture URL, deletion fields, security stamp, iCal token, and
    /// lockout. The source account is set to <c>"Merged User"</c> with a
    /// synthetic <c>@merged.local</c> email and a lockout end of
    /// <see cref="DateTimeOffset.MaxValue"/> so it cannot be logged into.
    /// Returns false if the user does not exist.
    /// </summary>
    Task<bool> AnonymizeForMergeAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Removes every <c>AspNetUserLogins</c> row for the given user. Used by
    /// <c>AccountMergeService.AcceptAsync</c> to prevent the anonymized
    /// source account from being logged into via its OAuth providers.
    /// </summary>
    Task RemoveExternalLoginsAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Migrates every external-login row from <paramref name="sourceUserId"/>
    /// to <paramref name="targetUserId"/>. If the target already has a login
    /// with the same <c>LoginProvider</c>, the source's row is dropped rather
    /// than duplicated. Used by <c>DuplicateAccountService.ResolveAsync</c>
    /// to re-link sign-in credentials before archiving the source account.
    /// </summary>
    Task MigrateExternalLoginsAsync(
        Guid sourceUserId, Guid targetUserId, CancellationToken ct = default);

    /// <summary>
    /// Sets <see cref="User.ContactSource"/> if and only if it is currently
    /// <c>null</c>. No-op if the user already has a <c>ContactSource</c> set
    /// or the user does not exist. Returns true when the source was set.
    /// </summary>
    Task<bool> SetContactSourceIfNullAsync(
        Guid userId, ContactSource source, CancellationToken ct = default);

    /// <summary>
    /// Purges (anonymizes + locks out) a user: removes all UserEmail rows for
    /// the user, overwrites <c>Email</c>/<c>NormalizedEmail</c>/<c>UserName</c>/
    /// <c>NormalizedUserName</c> with a sentinel <c>purged-{guid}@deleted.local</c>
    /// address, prepends "Purged" to the display name, and permanently locks
    /// out the account. Atomic: email removal and user anonymization happen in
    /// one <c>SaveChangesAsync</c>. Returns the original display name if the
    /// user was purged; null if the user did not exist.
    /// </summary>
    /// <remarks>
    /// Used by <c>IUserService.PurgeAsync</c>. Removes <c>UserEmail</c> rows so
    /// the unique-index constraint does not block a future account creation
    /// reusing the same email. Does not touch Profile or other section-owned
    /// rows — those are either retained (audit) or removed by cascades.
    /// </remarks>
    Task<string?> PurgeAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Returns the count of users with a non-null <c>DeletionRequestedAt</c>.
    /// </summary>
    Task<int> GetPendingDeletionCountAsync(CancellationToken ct = default);

    /// <summary>
    /// Sets <c>User.LastConsentReminderSentAt</c> to <paramref name="sentAt"/>.
    /// No-op if the user does not exist.
    /// </summary>
    Task SetLastConsentReminderSentAsync(
        Guid userId, Instant sentAt, CancellationToken ct = default);

    /// <summary>
    /// Returns the count of users whose <c>GoogleEmailStatus</c> equals
    /// <see cref="GoogleEmailStatus.Rejected"/>. Used by the admin digest.
    /// </summary>
    Task<int> GetRejectedGoogleEmailCountAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the ids of every user whose <c>DeletionScheduledFor</c> is at
    /// or before <paramref name="now"/> and whose <c>DeletionEligibleAfter</c>
    /// is either null or at or before <paramref name="now"/>. Used by the
    /// account deletion job to enumerate expired candidates.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetAccountsDueForAnonymizationAsync(
        Instant now, CancellationToken ct = default);

    /// <summary>
    /// Applies the identity-level fields of the GDPR expiry anonymization in
    /// one atomic save: renames the user to <c>Deleted User</c> + sentinel
    /// email, removes every <c>UserEmail</c> row, clears phone/picture/iCal
    /// token, clears all deletion fields, sets the security stamp, and
    /// permanently locks out the account. Returns a small summary of the
    /// prior identity (effective email, display name, preferred language) or
    /// <c>null</c> if the user does not exist. Used by the account deletion
    /// job via <see cref="AnonymizeExpiredAccountAsync"/>.
    /// </summary>
    Task<ExpiredDeletionAnonymizationResult?> ApplyExpiredDeletionAnonymizationAsync(
        Guid userId, CancellationToken ct = default);

    // ==========================================================================
    // Reads — EventParticipation
    // ==========================================================================

    /// <summary>
    /// Returns the participation record for a user/year, or null if none.
    /// Read-only (AsNoTracking).
    /// </summary>
    Task<EventParticipation?> GetParticipationAsync(
        Guid userId, int year, CancellationToken ct = default);

    /// <summary>
    /// Returns all participation records for a given year. Read-only (AsNoTracking).
    /// </summary>
    Task<IReadOnlyList<EventParticipation>> GetAllParticipationsForYearAsync(
        int year, CancellationToken ct = default);

    // ==========================================================================
    // Writes — EventParticipation
    // ==========================================================================

    /// <summary>
    /// Upserts a participation record. If a record exists for (userId, year):
    /// <list type="bullet">
    ///   <item>if its <see cref="ParticipationStatus"/> is <see cref="ParticipationStatus.Attended"/>,
    ///     the call is a no-op (Attended is permanent) — returns null;</item>
    ///   <item>otherwise, the status, source, and declaredAt are overwritten with
    ///     the provided values — returns the updated row.</item>
    /// </list>
    /// If no record exists, a new one is created with the provided values and
    /// persisted — returns the new row. The returned entity is detached
    /// (AsNoTracking semantics; the owning context is disposed before return).
    /// </summary>
    Task<EventParticipation?> UpsertParticipationAsync(
        Guid userId,
        int year,
        ParticipationStatus status,
        ParticipationSource source,
        Instant? declaredAt,
        CancellationToken ct = default);

    /// <summary>
    /// Removes the participation record for (userId, year) if and only if its
    /// source matches <paramref name="requiredSource"/> and its status is not
    /// <see cref="ParticipationStatus.Attended"/>. Returns true if a row was
    /// deleted.
    /// </summary>
    Task<bool> RemoveParticipationAsync(
        Guid userId,
        int year,
        ParticipationSource requiredSource,
        CancellationToken ct = default);

    /// <summary>
    /// Bulk import historical participation data (admin backfill). For each
    /// (userId, status) entry: if an existing Attended record exists for the
    /// year, skip it (Attended is permanent); otherwise upsert with
    /// <see cref="ParticipationSource.AdminBackfill"/> and <c>DeclaredAt = null</c>.
    /// Returns the number of entries processed (including skipped-for-Attended).
    /// </summary>
    Task<int> BackfillParticipationsAsync(
        int year,
        IReadOnlyList<(Guid UserId, ParticipationStatus Status)> entries,
        CancellationToken ct = default);
}

/// <summary>
/// Slice returned from <see cref="IUserRepository.ApplyExpiredDeletionAnonymizationAsync"/>
/// so the service layer can send the confirmation email and log audit entries
/// without re-loading the (now anonymized) user.
/// </summary>
/// <param name="OriginalEmail">
/// The effective email on the account before anonymization (preferring the
/// verified notification-target <c>UserEmail</c> row, falling back to
/// <c>User.Email</c>). May be null when the account never had an email.
/// </param>
/// <param name="OriginalDisplayName">Display name on the user before the write.</param>
/// <param name="PreferredLanguage">Preferred language on the user before the write.</param>
public record ExpiredDeletionAnonymizationResult(
    string? OriginalEmail,
    string OriginalDisplayName,
    string PreferredLanguage);
