using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.Interfaces.Users;

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

    /// <summary>
    /// Returns all users, read-only. At ~500 users this is cheap to load in full.
    /// Used by admin list views that must include profileless users.
    /// </summary>
    Task<IReadOnlyList<User>> GetAllUsersAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the ids of every user in the system, read-only. Used by the
    /// admin dashboard to partition all users without loading the full graph.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetAllUserIdsAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the language distribution for the given user ids, grouped by
    /// <see cref="User.PreferredLanguage"/>. Used by the admin dashboard
    /// to render language stats for approved humans.
    /// </summary>
    Task<IReadOnlyList<(string Language, int Count)>>
        GetLanguageDistributionForUserIdsAsync(
            IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);

    /// <summary>
    /// Purges a human: removes all UserEmail rows for the user, anonymizes the
    /// email/display name, and permanently locks out the account. Returns
    /// false if the user did not exist. Invalidates the FullProfile cache on
    /// success so downstream consumers see the purged view.
    /// </summary>
    Task<(bool Purged, string? DisplayName)> PurgeAsync(Guid userId, CancellationToken ct = default);

    // ---- Methods added for Profile-section migration (§15 Step 0) ----

    /// <summary>
    /// Sets <c>User.GoogleEmail</c> if it is currently null. No-op if the
    /// user already has a GoogleEmail set or the user does not exist.
    /// Returns true if the GoogleEmail was set.
    /// </summary>
    Task<bool> TrySetGoogleEmailAsync(Guid userId, string email, CancellationToken ct = default);

    /// <summary>
    /// Unconditionally sets <c>User.GoogleEmail</c>, overwriting any existing
    /// value. Used by <c>EmailProvisioningService</c> after a successful
    /// Google Workspace provisioning, where the new <c>@nobodies.team</c>
    /// address must become the authoritative Google identity even if the
    /// user previously signed in with a personal Google account. Returns
    /// true if the user exists and the value was written, false if the user
    /// does not exist.
    /// </summary>
    Task<bool> SetGoogleEmailAsync(Guid userId, string email, CancellationToken ct = default);

    /// <summary>
    /// Sets <see cref="User.GoogleEmailStatus"/> to <paramref name="status"/>.
    /// Used by <c>GoogleWorkspaceSyncService</c> to mark a user's Google email
    /// as <see cref="GoogleEmailStatus.Rejected"/> after Google returns HTTP 403
    /// during a group-add, indicating the email address is not associated with
    /// a Google account. Returns true when a write occurred (user exists and
    /// status actually changed), false otherwise.
    /// </summary>
    Task<bool> SetGoogleEmailStatusAsync(
        Guid userId, GoogleEmailStatus status, CancellationToken ct = default);

    /// <summary>
    /// Rewrites <c>User.Email</c>, <c>User.UserName</c>, and their normalized
    /// counterparts to <paramref name="newEmail"/>. If the user has an
    /// OAuth-sourced <see cref="UserEmail"/> row, it is
    /// rewritten to match so login continues to succeed against the new email.
    /// Invalidates the Profile cache on success. Returns the previous email on
    /// success, or (<c>false</c>, <c>null</c>) if the user does not exist.
    /// </summary>
    Task<(bool Updated, string? OldEmail)> ApplyEmailBackfillAsync(
        Guid userId, string newEmail, CancellationToken ct = default);

    /// <summary>
    /// Updates <c>User.DisplayName</c>. No-op if the user does not exist.
    /// </summary>
    Task UpdateDisplayNameAsync(Guid userId, string displayName, CancellationToken ct = default);

    /// <summary>
    /// Sets the deletion-pending fields on a user (<c>DeletionRequestedAt</c>,
    /// <c>DeletionScheduledFor</c>). Returns false if the user does not exist.
    /// </summary>
    Task<bool> SetDeletionPendingAsync(Guid userId, Instant requestedAt, Instant scheduledFor, CancellationToken ct = default);

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
    /// Returns all contact users (ContactSource != null, LastLoginAt == null),
    /// optionally filtered by display name or email search term.
    /// Ordered by CreatedAt descending.
    /// </summary>
    Task<IReadOnlyList<User>> GetContactUsersAsync(string? search, CancellationToken ct = default);

    /// <summary>
    /// Returns the <c>LastLoginAt</c> timestamp of every user whose last login falls
    /// within the half-open window <c>[fromInclusive, toExclusive)</c>. Used by the
    /// shift coordinator dashboard to chart distinct logins by day without reading
    /// the users table directly.
    /// </summary>
    Task<IReadOnlyList<Instant>> GetLoginTimestampsInWindowAsync(
        Instant fromInclusive,
        Instant toExclusive,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the id of any user, other than <paramref name="excludeUserId"/>,
    /// whose <c>GoogleEmail</c> matches <paramref name="email"/> (case-insensitive),
    /// or null if no such user exists. Used by @nobodies.team provisioning so
    /// the Application-layer service can detect cross-user conflicts without
    /// touching the database directly.
    /// </summary>
    Task<Guid?> GetOtherUserIdHavingGoogleEmailAsync(
        string email,
        Guid excludeUserId,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the count of users with a non-null <c>DeletionRequestedAt</c>.
    /// Used by the notification meter to surface pending account deletions
    /// to Admin without letting the Notifications section read the users
    /// table directly (design-rules §2c).
    /// </summary>
    Task<int> GetPendingDeletionCountAsync(CancellationToken ct = default);

    /// <summary>
    /// Sets <c>User.LastConsentReminderSentAt</c> to <paramref name="sentAt"/>.
    /// No-op if the user does not exist. Used by the re-consent reminder job
    /// so it does not write to the Users table directly (design-rules §2c).
    /// </summary>
    Task SetLastConsentReminderSentAsync(
        Guid userId, Instant sentAt, CancellationToken ct = default);

    /// <summary>
    /// Returns the count of users whose <see cref="User.GoogleEmailStatus"/>
    /// equals <see cref="Humans.Domain.Enums.GoogleEmailStatus.Rejected"/>.
    /// Used by the admin daily digest so the job does not read the users
    /// table directly (design-rules §2c).
    /// </summary>
    Task<int> GetRejectedGoogleEmailCountAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the user ids of every account with <c>DeletionScheduledFor</c>
    /// in the past (or equal to <paramref name="now"/>) and with
    /// <c>DeletionEligibleAfter</c> either null or already elapsed. Used by
    /// the account deletion job to enumerate candidates without reading the
    /// Users table directly (design-rules §2c).
    /// </summary>
    Task<IReadOnlyList<Guid>> GetAccountsDueForAnonymizationAsync(
        Instant now, CancellationToken ct = default);

    /// <summary>
    /// Anonymizes the identity portion of a user whose 30-day deletion grace
    /// period has expired: display name, username, email fields, profile
    /// picture, phone number, deletion fields, security stamp, iCal token,
    /// and lockout are all cleared or replaced with sentinels. Removes
    /// <c>UserEmail</c> rows, ends active team memberships (via
    /// <see cref="Teams.ITeamService.RevokeAllMembershipsAsync"/>) and active
    /// governance role assignments, anonymizes the profile (via
    /// <see cref="Profiles.IProfileService"/>), removes contact fields,
    /// volunteer history, volunteer event profiles (via
    /// <see cref="Shifts.IShiftManagementService"/>) and cancels active shift
    /// signups (via <see cref="Shifts.IShiftSignupService"/>). Returns an
    /// <see cref="AnonymizedAccountSummary"/> describing the prior identity,
    /// the user's preferred language, and the ids of any shift signups that
    /// were cancelled, or null if the user does not exist. Invalidates the
    /// FullProfile cache, role-assignment claims, shift-authorization, and
    /// the Teams member cache.
    /// </summary>
    Task<AnonymizedAccountSummary?> AnonymizeExpiredAccountAsync(
        Guid userId, Instant now, CancellationToken ct = default);
}

/// <summary>
/// Summary returned from <see cref="IUserService.AnonymizeExpiredAccountAsync"/>
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
