using Humans.Users.Contracts;
using NodaTime;
using Humans.Base.Interfaces.Repositories;

namespace Humans.Users.Data.Repositories;

/// <summary>
/// Repository for the <c>AspNetUsers</c> (via <see cref="User"/>) and
/// <c>event_participations</c> tables. The only non-test file that may write
/// to those DbSets after the User migration lands.
/// </summary>
/// <remarks>
/// Read methods are <c>AsNoTracking</c>. Narrow-field updates commit atomically
/// in a single <see cref="Microsoft.EntityFrameworkCore.IDbContextFactory{UsersDbContext}"/>-owned
/// context. Event-participation mutations expose load-then-save primitives so
/// <see cref="Humans.Application.Services.Users.UserService"/> can apply the
/// status/source business rules before persisting.
/// </remarks>
internal partial interface IUserRepository : IRepository
{
    // ==========================================================================
    // Reads — User
    // ==========================================================================

    /// <summary>
    /// Loads a single user by id. Read-only (AsNoTracking). Returns null if
    /// the user does not exist. <see cref="User.UserEmails"/> is owned by
    /// the UserEmail methods on this repository and is not populated by this method.
    /// </summary>
    Task<User?> GetByIdAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Loads every user, read-only (AsNoTracking). Used by admin list views
    /// that must include profileless users. Trivial at ~500-user scale.
    /// </summary>
    Task<IReadOnlyList<User>> GetAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Finds a user whose legacy <c>GoogleEmail</c> shadow column matches the given
    /// normalized address (case-insensitive). If <paramref name="alternateEmail"/>
    /// is non-null, also matches the alternate form. Canonical
    /// <c>user_emails</c> matching is owned by the UserEmail methods on this repository.
    /// Read-only.
    /// </summary>
    Task<User?> GetByEmailOrAlternateAsync(
        string normalizedEmail, string? alternateEmail, CancellationToken ct = default);

    // ==========================================================================
    // Writes — User (atomic field updates)
    // ==========================================================================

    /// <summary>
    /// Updates <c>User.DisplayName</c>. Returns false if the user does not exist.
    /// </summary>
    Task<bool> UpdateDisplayNameAsync(Guid userId, string displayName, CancellationToken ct = default);

    /// <summary>
    /// Sets <c>User.PreferredLanguage</c>. Returns false if the user does not exist.
    /// </summary>
    Task<bool> SetPreferredLanguageAsync(Guid userId, string preferredLanguage, CancellationToken ct = default);

    /// <summary>
    /// Sets <c>User.ICalToken</c>. Returns false if the user does not exist.
    /// </summary>
    Task<bool> SetICalTokenAsync(Guid userId, Guid token, CancellationToken ct = default);

    /// <summary>
    /// Stamps <c>User.LastLoginAt</c>. Returns false if the user does not exist.
    /// </summary>
    Task<bool> SetLastLoginAsync(Guid userId, Instant at, CancellationToken ct = default);

    /// <summary>
    /// Sets the deletion-pending fields on a user (<c>DeletionRequestedAt</c>,
    /// <c>DeletionScheduledFor</c>, optional <c>DeletionEligibleAfter</c>).
    /// Returns false if the user does not exist.
    /// </summary>
    Task<bool> SetDeletionPendingAsync(
        Guid userId, Instant requestedAt, Instant scheduledFor, Instant? eligibleAfter,
        CancellationToken ct = default);

    /// <summary>
    /// Applies a suspension transition directly to <see cref="Domain.Entities.User.State"/>.
    /// Unsuspending re-classifies from the remaining fields (rejected/deletion/name/merge).
    /// Writes <c>users.State</c> only — the suspension reason belongs to the audit log and the
    /// notification, not to a second copy on the profile.
    /// </summary>
    /// <returns>true if a row was updated; false if the user does not exist.</returns>
    Task<bool> SetSuspensionAsync(
        Guid userId, bool suspended, bool adminSuspension, CancellationToken ct = default);

    /// <summary>
    /// Clears deletion-pending fields (<c>DeletionRequestedAt</c>,
    /// <c>DeletionScheduledFor</c>, <c>DeletionEligibleAfter</c>).
    /// Returns false if the user does not exist.
    /// </summary>
    Task<bool> ClearDeletionAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Tombstones <paramref name="sourceUserId"/> as merged into
    /// <paramref name="targetUserId"/>. Sets <c>MergedToUserId</c> +
    /// <c>MergedAt</c>, anonymizes the identity portion of the user row
    /// (display name, profile picture URL, phone, security stamp, iCal
    /// token, deletion fields), and locks the source out
    /// (<c>LockoutEnd = DateTimeOffset.MaxValue</c>) so it cannot be
    /// logged into. Returns false if the user does not exist.
    /// </summary>
    Task<bool> AnonymizeForMergeAsync(
        Guid sourceUserId, Guid targetUserId, Instant now,
        CancellationToken ct = default);

    /// <summary>
    /// Returns userIds of users that have at least one row in
    /// <c>AspNetUserLogins</c>. Used by <c>IUserService</c> together with
    /// UserEmail methods on this repository to surface ghost auth artifacts.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetUserIdsWithExternalLoginsAsync(CancellationToken ct = default);

    /// <summary>
    /// Permanently deletes users after the caller has cleared cross-section
    /// references and UserEmail rows. Also removes AspNetUserLogins rows for
    /// those users. Returns the number of user rows deleted.
    /// </summary>
    Task<int> DeleteUsersAsync(
        IReadOnlyCollection<Guid> userIds,
        CancellationToken ct = default);

    /// <summary>
    /// Returns every <c>AspNetUserLogins</c> <c>(LoginProvider, ProviderKey)</c>
    /// row for each of the given users, grouped by <c>UserId</c>. Users without
    /// any external login are absent from the dictionary. Used by the admin
    /// per-user emails diagnostic to show the OAuth identity store alongside
    /// the <c>UserEmail</c> tag rows.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<(string Provider, string ProviderKey)>>>
        GetExternalLoginsByUserIdsAsync(
            IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);

    /// <summary>
    /// Migrates every <c>AspNetUserLogins</c> row from
    /// <paramref name="sourceUserId"/> to <paramref name="targetUserId"/>.
    /// <c>IdentityUserLogin&lt;Guid&gt;</c>'s primary key is
    /// (<c>LoginProvider</c>, <c>ProviderKey</c>) only — <c>UserId</c> is
    /// just an FK column — so two users can never share a row at the DB
    /// level, and no de-duplication is possible. Returns the count of
    /// logins now attributed to the target. Used by the account-merge
    /// fan-out to re-link sign-in credentials before archiving the source
    /// account.
    /// </summary>
    Task<int> ReassignLoginsToUserAsync(
        Guid sourceUserId, Guid targetUserId, CancellationToken ct = default);

    /// <summary>
    /// Migrates every <c>event_participations</c> row from
    /// <paramref name="sourceUserId"/> to <paramref name="targetUserId"/>.
    /// On (Year, UserId) collision, keeps the row with the highest
    /// <see cref="ParticipationStatus"/> per the precedence
    /// <c>Attended &gt; Ticketed &gt; NoShow &gt; NotAttending</c>.
    /// Returns the count of rows now attributed to the target. Used by
    /// <c>AccountMergeService.AcceptAsync</c>.
    /// </summary>
    Task<int> ReassignEventParticipationToUserAsync(
        Guid sourceUserId, Guid targetUserId, CancellationToken ct = default);

    /// <summary>
    /// Sets <see cref="User.ContactSource"/> if and only if it is currently
    /// <c>null</c>. No-op if the user already has a <c>ContactSource</c> set
    /// or the user does not exist. Returns true when the source was set.
    /// </summary>
    Task<bool> SetContactSourceIfNullAsync(
        Guid userId, ContactSource source, CancellationToken ct = default);

    /// <summary>
    /// Sets <c>User.LastConsentReminderSentAt</c> to <paramref name="sentAt"/>.
    /// No-op if the user does not exist.
    /// </summary>
    Task SetLastConsentReminderSentAsync(
        Guid userId, Instant sentAt, CancellationToken ct = default);

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
    /// one save: renames the user to <c>Deleted User</c> + sentinel
    /// email, removes every <c>AspNetUserLogins</c> row, clears
    /// phone/picture/iCal token, clears
    /// all deletion fields, sets the security stamp, and permanently locks
    /// out the account. Returns a small summary of the prior identity
    /// (effective email, display name, preferred language) or <c>null</c> if
    /// the user does not exist. Used by the account deletion job via
    /// <see cref="AnonymizeExpiredAccountAsync"/>.
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
    /// Returns all participation records for a given user (across all years),
    /// ordered by year ascending. Read-only (AsNoTracking). Used by the GDPR
    /// export contributor under <c>GdprExportSections.EventParticipations</c>.
    /// </summary>
    Task<IReadOnlyList<EventParticipation>> GetEventParticipationsByUserIdAsync(
        Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Bulk-loads all <c>event_participations</c> rows for the given userIds,
    /// grouped by <c>UserId</c>. Users with no participations are absent from
    /// the dictionary. Read-only (AsNoTracking). Used by
    /// <c>CachingUserService.WarmAllAsync</c> to avoid N+1 per-user fetches
    /// when populating the UserInfo cache for every user at startup.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<EventParticipation>>>
        GetEventParticipationsByUserIdsAsync(
            IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);

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
    /// <para>
    /// <paramref name="checkedInAt"/> is the vendor-reported gate-arrival
    /// instant carried from <see cref="ParticipationSource.TicketSync"/>. Set
    /// on row creation, and on the no-existing-row → Attended path. NEVER
    /// overwritten once non-null — Attended-permanence applies to the
    /// timestamp as well (issue nobodies-collective/Humans#736).
    /// </para>
    /// </summary>
    Task<EventParticipation?> UpsertParticipationAsync(
        Guid userId,
        int year,
        ParticipationStatus status,
        ParticipationSource source,
        Instant? declaredAt,
        Instant? checkedInAt,
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
