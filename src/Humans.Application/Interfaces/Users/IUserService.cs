using Humans.Application.Architecture;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.Interfaces.Users;

/// <summary>
/// Service owning user-level concerns. Currently focused on event participation.
/// </summary>
/// <remarks>
/// Surface-budget recent history (newest first):
/// <list type="bullet">
///   <item>32→28 — UserInfo snapshot consolidation (PR #553): removed 4 single-caller DB readers (GetRejectedGoogleEmailCountAsync, GetCountByContactSourceAsync, GetLoginTimestampsInWindowAsync, GetParticipationAsync); reimplemented 2 multi-caller readers (GetAllParticipationsForYearAsync, GetMergedSourceIdsAsync) over the cached UserInfo snapshot via the CachingUserService decorator. Inner UserService now throws NotSupportedException on the swapped methods to match the existing GetAllUserInfos pattern. Net effect: 7 DB read paths drained, one cache snapshot.</item>
///   <item>33→32 — admin dashboard language tile (PR #553 follow-up): removed GetLanguageDistributionForUserIdsAsync. Sole caller (AdminDashboardService) now groups in memory over the cached UserInfo snapshot rather than a per-render SQL GROUP BY against `users` — eliminates a DB round-trip on every admin dashboard render.</item>
///   <item>32→33 — admin stats + /Users/Admin/Debug + /Tickets Venn: added GetAllUserInfos. Snapshot accessor — the cache is the canonical read-model; all aggregate consumers read from it rather than re-querying the underlying tables.</item>
///   <item>32→31 — mailer-inbound-import follow-up: removed GetDisplayNamesByIdsAsync. HumanViewComponent already renders the cached DisplayName from a userId Guid; pre-fetching the dictionary was redundant.</item>
///   <item>33→32 — merge with main: issue-695 HUM0009 service-DbContext analyzer PR landed on main with net -1 (removed two [Obsolete] Google-email methods TrySetGoogleEmailAsync + SetGoogleEmailAsync; added DeleteUsersAsync for admin dev-reset).</item>
///   <item>32→33 — mailer-inbound-import: added GetDisplayNamesByIdsAsync for import preview — batch DisplayName lookup keyed by user id.</item>
///   <item>31→32 — mailer-inbound-import: added GetCountByContactSourceAsync for admin dashboard per-source import totals.</item>
///   <item>2026-05-11 — InterfaceMethodBudgetTests retired; budget migrated to [SurfaceBudget(31)] (issue nobodies-collective/Humans#700).</item>
///   <item>30→31 — issue-660 EmailProblems case 8 cleanup: added DeleteAllExternalLoginsForUserAsync — service surface for the admin "Delete ghost logins" action. Auth-table cleanup; no expiable substitute (only the User section can write to AspNetUserLogins).</item>
///   <item>29→30 — issue-660 EmailProblems case 8: added GetUsersWithLoginsButNoEmailsAsync to surface ghost AspNetUserLogins rows. Authorized by repo owner — no expiable substitute exists at the service surface (UserLogins is auth-internal).</item>
///   <item>31→29 — account-merge fold final consolidation: removed ReassignLoginsToUserAsync and ReassignEventParticipationToUserAsync from IUserService. Both moves now happen through IUserMerge.ReassignAsync on UserService; DuplicateAccountService routes the logins move directly via IUserRepository.</item>
///   <item>31→31 — account-merge fold redesign Phase 4.1: added GetMergedSourceIdsAsync (the chain-follow service primitive AuditLog/Consent/BudgetAuditLog reads call to surface rows still attributed to merged source tombstones); removed GetPendingDeletionCountAsync. Three callers derive the count in-memory from the full user list per design-rules in-memory caching guidance.</item>
///   <item>31→31 — account-merge fold redesign Phase 3.4: added 3 fold primitives (AnonymizeForMergeAsync, ReassignLoginsToUserAsync, ReassignEventParticipationToUserAsync); removed 3 to match: SetGoogleEmailStatusAsync (interface-surface-dead), BackfillNobodiesTeamGoogleEmailsAsync (sole caller now iterates per-user via IUserEmailService.TryBackfillGoogleEmailAsync), GetAllUserIdsAsync (callers derive ids from GetAllUsersAsync).</item>
///   <item>-1 GetContactUsersAsync removed (/Contacts surface deleted in PR 2 of email-identity-decoupling — only ContactService called it).</item>
/// </list>
/// </remarks>
[SurfaceBudget(28)]
public interface IUserService : IApplicationService, IUserMerge
{
    /// <summary>
    /// Returns the unified <see cref="UserInfo"/> read-model for the given
    /// user, stitched from <c>users</c>, <c>user_emails</c>,
    /// <c>event_participations</c>, <c>user_logins</c>, <c>profiles</c>,
    /// <c>contact_fields</c>, <c>profile_languages</c>, and
    /// <c>volunteer_history_entries</c>. Issue #703: the caching decorator
    /// serves dict hits synchronously; the inner service rebuilds from
    /// repositories on miss.
    /// </summary>
    ValueTask<UserInfo?> GetUserInfoAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Returns a snapshot of every cached <see cref="UserInfo"/>. The cache is
    /// the canonical "everything-about-a-person" source; admin stat tiles,
    /// debug surfaces, and cross-section aggregates read from this snapshot
    /// rather than re-querying the contributing tables. Returns a new
    /// collection per call — the underlying dictionary is mutable and callers
    /// iterate without locking.
    /// </summary>
    IReadOnlyCollection<UserInfo> GetAllUserInfos();

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
    /// Same as <see cref="GetByIdsAsync"/> but also hydrates each user's
    /// <see cref="User.UserEmails"/> collection so callers can resolve
    /// <see cref="User.GetEffectiveEmail"/> (the verified notification-target
    /// address) without a second round-trip. Used by notification-sending
    /// jobs (digests, re-consent reminder, term renewal) so the correct
    /// recipient is picked instead of silently falling back to
    /// <see cref="User.Email"/>.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, User>> GetByIdsWithEmailsAsync(
        IReadOnlyCollection<Guid> userIds,
        CancellationToken ct = default);

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

    /// <summary>
    /// Returns all users, read-only. At ~500 users this is cheap to load in full.
    /// Used by admin list views that must include profileless users.
    /// </summary>
    Task<IReadOnlyList<User>> GetAllUsersAsync(CancellationToken ct = default);

    /// <summary>
    /// Purges a human at the User aggregate — removes all UserEmail rows for
    /// the user, anonymizes the email/display name, and permanently locks out
    /// the account. Returns the prior display name on success, or <c>null</c>
    /// if the user did not exist. Invalidates the FullProfile cache on
    /// success so downstream consumers see the purged view. Cross-section
    /// invalidation (ActiveTeams cache, etc.) is owned by the caller —
    /// <see cref="IAccountDeletionService.PurgeAsync"/> is the orchestrator
    /// wrapping this method for the admin-initiated purge flow.
    /// </summary>
    Task<string?> PurgeOwnDataAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Applies the identity-level fields of the GDPR expiry anonymization
    /// on the User aggregate in one atomic save — renames to
    /// <c>Deleted User</c> + sentinel email, removes <c>UserEmail</c> rows,
    /// clears phone/picture/iCal/deletion fields, sets the security stamp,
    /// and permanently locks out the account. Returns the pre-write identity
    /// slice or <c>null</c> if the user does not exist. Invalidates the
    /// FullProfile cache on success. Cross-section cascade (team
    /// memberships, role assignments, profile anonymization, shift cleanup)
    /// is owned by <see cref="IAccountDeletionService.AnonymizeExpiredAccountAsync"/>.
    /// </summary>
    Task<ExpiredDeletionAnonymizationResult?> ApplyExpiredDeletionAnonymizationAsync(
        Guid userId, CancellationToken ct = default);

    // ---- Methods added for Profile-section migration (§15 Step 0) ----

    /// <summary>
    /// Sync-driven <see cref="User.GoogleEmailStatus"/> write that preserves
    /// the "Rejected is terminal" invariant: once flagged
    /// <see cref="GoogleEmailStatus.Rejected"/> (Google HTTP 403 on a
    /// group-add), a later successful sync MUST NOT flip the user back to
    /// <see cref="GoogleEmailStatus.Valid"/> until they change their email.
    /// Call this from any outbox-processor / reconciliation writer; the
    /// invariant lives here so a future second caller cannot silently bypass
    /// it. Returns true if a write occurred, false if short-circuited by
    /// the rule or the user does not exist.
    /// </summary>
    Task<bool> TrySetGoogleEmailStatusFromSyncAsync(
        Guid userId, GoogleEmailStatus status, CancellationToken ct = default);

    /// <summary>
    /// Updates <c>User.DisplayName</c>. No-op if the user does not exist.
    /// </summary>
    Task UpdateDisplayNameAsync(Guid userId, string displayName, CancellationToken ct = default);

    /// <summary>
    /// Sets the deletion-pending fields on a user (<c>DeletionRequestedAt</c>,
    /// <c>DeletionScheduledFor</c>, optional <c>DeletionEligibleAfter</c>).
    /// <paramref name="eligibleAfter"/> is the post-event hold date when the
    /// user is on a current event ticket, otherwise null. Returns false if
    /// the user does not exist.
    /// </summary>
    Task<bool> SetDeletionPendingAsync(
        Guid userId, Instant requestedAt, Instant scheduledFor, Instant? eligibleAfter,
        CancellationToken ct = default);

    /// <summary>
    /// Clears deletion-pending fields (<c>DeletionRequestedAt</c>,
    /// <c>DeletionScheduledFor</c>, <c>DeletionEligibleAfter</c>).
    /// Returns false if the user does not exist.
    /// </summary>
    Task<bool> ClearDeletionAsync(Guid userId, CancellationToken ct = default);

    // ---- Methods added for ContactService migration ----

    /// <summary>
    /// Finds a user whose <c>Email</c> or <c>GoogleEmail</c> matches the given
    /// address (case-insensitive). Also checks the gmail/googlemail alternate
    /// when applicable. Returns null if no match.
    /// </summary>
    Task<User?> GetByEmailOrAlternateAsync(string email, CancellationToken ct = default);

    /// <summary>
    /// Returns the id of any user, other than <paramref name="excludeUserId"/>,
    /// whose legacy <c>User.GoogleEmail</c> shadow column matches <paramref name="email"/>
    /// (case-insensitive), or null if no such user exists.
    /// </summary>
    [Obsolete("Issue nobodies-collective/Humans#687: User.GoogleEmail is being deprecated. Use IUserEmailService.GetOtherUserIdHavingEmailAsync — once UserEmail.IsGoogle is sole source of truth a Google identity always has a matching user_emails row, so any other user already owning the address is detected by the user_emails check.")]
    Task<Guid?> GetOtherUserIdHavingGoogleEmailAsync(
        string email,
        Guid excludeUserId,
        CancellationToken ct = default);

    /// <summary>
    /// Sets <c>User.LastConsentReminderSentAt</c> to <paramref name="sentAt"/>.
    /// No-op if the user does not exist. Used by the re-consent reminder job
    /// so it does not write to the Users table directly (design-rules §2c).
    /// </summary>
    Task SetLastConsentReminderSentAsync(
        Guid userId, Instant sentAt, CancellationToken ct = default);

    /// <summary>
    /// Returns the user ids of every account with <c>DeletionScheduledFor</c>
    /// in the past (or equal to <paramref name="now"/>) and with
    /// <c>DeletionEligibleAfter</c> either null or already elapsed. Used by
    /// the account deletion job to enumerate candidates without reading the
    /// Users table directly (design-rules §2c).
    /// </summary>
    Task<IReadOnlyList<Guid>> GetAccountsDueForAnonymizationAsync(
        Instant now, CancellationToken ct = default);

    // ---- Methods added for AccountMergeService fold-into-target redesign ----

    /// <summary>
    /// Tombstones source user as merged into target. Sets
    /// <c>MergedToUserId</c>, <c>MergedAt</c>, locks the source out
    /// (<c>LockoutEnd</c> far future), and applies the existing per-user
    /// anonymization fields (display name, picture, phone, security stamp,
    /// iCal token). Returns true if the source row existed; false if it
    /// was missing. Invalidates the FullProfile cache for the source on
    /// success. Used by <c>AccountMergeService.AcceptAsync</c> as the
    /// final step of the fold-into-target flow.
    /// </summary>
    Task<bool> AnonymizeForMergeAsync(
        Guid sourceUserId, Guid targetUserId, Instant now,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the set of source-tombstone ids whose <c>MergedToUserId</c>
    /// equals <paramref name="targetUserId"/>. Single canonical chain-follow
    /// primitive: AuditLog, Consent, BudgetAuditLog reads call this rather
    /// than each section reinventing the lookup. Set is small (typically
    /// zero, usually one).
    /// </summary>
    Task<IReadOnlySet<Guid>> GetMergedSourceIdsAsync(
        Guid targetUserId, CancellationToken ct = default);

    /// <summary>
    /// Returns userIds of users that have AspNetUserLogins rows but zero
    /// UserEmail rows. Used by EmailProblems admin scan.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetUsersWithLoginsButNoEmailsAsync(CancellationToken ct = default);

    /// <summary>
    /// Permanently deletes the requested user rows after the caller has cleared
    /// cross-section references. Also removes the users' email rows and
    /// external login rows. Requires the current authenticated user to hold
    /// the full Admin role.
    /// </summary>
    Task<int> DeleteUsersAsync(
        IReadOnlyCollection<Guid> userIds,
        CancellationToken ct = default);

    /// <summary>
    /// Deletes every <c>AspNetUserLogins</c> row for the given user. Returns the
    /// number of rows deleted. Used by EmailProblems ghost-login cleanup.
    /// </summary>
    Task<int> DeleteAllExternalLoginsForUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Returns every <c>AspNetUserLogins</c> <c>(LoginProvider, ProviderKey)</c>
    /// row for each of the given users, grouped by <c>UserId</c>. Users without
    /// any external login are absent from the dictionary. Used by the per-user
    /// admin emails diagnostic and the OAuth-reconcile mother-of-all
    /// cross-user-collision log.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<(string Provider, string ProviderKey)>>>
        GetExternalLoginsByUserIdsAsync(
            IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);
}

/// <summary>
/// Summary returned from <see cref="IAccountDeletionService.AnonymizeExpiredAccountAsync"/>
/// so the caller (account deletion job) can send a confirmation email and
/// write the corresponding audit log entries without re-loading the
/// anonymized row.
/// </summary>
/// <param name="OriginalEmail">
/// The effective email on the account before anonymization. May be null
/// when the account never had an email set.
/// </param>
/// <param name="OriginalDisplayName">
/// The display name on the account before anonymization.
/// </param>
/// <param name="PreferredLanguage">
/// The user's preferred language, used to render the confirmation email.
/// </param>
/// <param name="CancelledSignupIds">
/// The id of each shift signup that was cancelled as part of the
/// anonymization, paired with the shift it belonged to. Used by the caller
/// to emit per-signup audit entries.
/// </param>
public record AnonymizedAccountSummary(
    string? OriginalEmail,
    string OriginalDisplayName,
    string PreferredLanguage,
    IReadOnlyList<(Guid SignupId, Guid ShiftId)> CancelledSignupIds);
