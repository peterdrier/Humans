using Humans.Onboarding.Contracts;
using NodaTime;

namespace Humans.Users.Contracts;

/// <summary>
/// The Users writes other sections, Web and Development genuinely call: the User, Profile and
/// UserEmail row writes. The section's own writes live on the internal <c>IUserManagement</c>,
/// which inherits this.
/// </summary>
// No marker interface — see Humans.Users.Contracts.csproj.
public interface IUserWrite
{
    /// <summary>
    /// Set participation status from ticket sync. Handles the lifecycle rules:
    /// - Valid ticket → Ticketed (<paramref name="checkedInAt"/> ignored)
    /// - Checked in → Attended (permanent)
    /// - Ticket purchase overrides NotAttending
    /// <para>
    /// <paramref name="checkedInAt"/> is the vendor-reported gate-arrival
    /// instant. Stored on <see cref="EventParticipation.CheckedInAt"/> when an
    /// Attended row is created, upgraded, or has no arrival timestamp yet. Never overwritten once
    /// non-null — matches the "Attended is permanent" invariant (issue
    /// nobodies-collective/Humans#736).
    /// </para>
    /// </summary>
    Task SetParticipationFromTicketSyncAsync(
        Guid userId,
        int year,
        ParticipationStatus status,
        Instant? checkedInAt,
        CancellationToken ct = default);

    /// <summary>
    /// Remove a TicketSync-sourced participation record when a user no longer has valid tickets.
    /// Does not remove UserDeclared or AdminBackfill records.
    /// Does not remove Attended records (permanent).
    /// </summary>
    Task RemoveTicketSyncParticipationAsync(Guid userId, int year, CancellationToken ct = default);

    // ---- User storage commands ----

    /// <summary>
    /// Sync-driven Google status write targeting the user's canonical verified
    /// <see cref="UserEmail.IsGoogle"/> address — status is per-address (#687),
    /// not on the user. Preserves the "Rejected is terminal" invariant: once the
    /// address is flagged <see cref="GoogleEmailStatus.Rejected"/> (Google HTTP
    /// 403 on a group/drive add), a later successful sync MUST NOT flip that same
    /// address back to <see cref="GoogleEmailStatus.Valid"/>. The user clears it
    /// by selecting a different Google email — a fresh row that starts
    /// <see cref="GoogleEmailStatus.Unknown"/> — so a rejection never strands sync
    /// after the address changes. Call this from any outbox-processor /
    /// reconciliation writer; the invariant lives here so a future second caller
    /// cannot silently bypass it. Returns true if a write occurred, false if
    /// short-circuited by the rule or the target row no longer exists. When
    /// <c>googleEmailId</c> is supplied, updates that verified row owned by the user,
    /// even if another address has since become their selected Google email.
    /// </summary>
    Task<bool> TrySetGoogleEmailStatusFromSyncAsync(
        Guid userId, GoogleEmailStatus status, CancellationToken ct = default, Guid? googleEmailId = null);

    /// <summary>
    /// Sets <c>User.PreferredLanguage</c>. Invalidates the UserInfo cache on
    /// success. No-op if the user does not exist.
    /// </summary>
    Task SetPreferredLanguageAsync(Guid userId, string preferredLanguage, CancellationToken ct = default);

    /// <summary>
    /// Stamps <c>User.LastLoginAt = now</c> after a successful sign-in (any
    /// method: OAuth, magic link, gate terminal) and refreshes the cached
    /// UserInfo. The single owner of the login-stamp rule — sign-in flows must
    /// not write <c>LastLoginAt</c> directly. No-op if the user does not exist.
    /// </summary>
    Task RecordLoginAsync(Guid userId, CancellationToken ct = default);

    // ---- Profile storage commands ----

    /// <summary>
    /// Updates <c>Profile.MembershipTier</c> on the user's profile.
    /// Returns false when no profile exists.
    /// </summary>
    Task<bool> SetMembershipTierAsync(
        Guid userId,
        MembershipTier tier,
        CancellationToken ct = default);

    /// <summary>
    /// Applies a consolidated onboarding/profile-state mutation to the user's
    /// profile. Audit/logging policy remains with the caller.
    /// </summary>
    Task<OnboardingResult> ApplyProfileOnboardingMutationAsync(
        Guid userId,
        UserProfileOnboardingCommand command,
        CancellationToken ct = default);

    /// <summary>
    /// Downgrades expired membership tiers and returns each user id with the
    /// tier that was written.
    /// </summary>
    Task<IReadOnlyList<(Guid UserId, MembershipTier NewTier)>>
        DowngradeMembershipTierForExpiredAsync(
            MembershipTier currentTier,
            IReadOnlyCollection<Guid> userIdsToKeep,
            IReadOnlyDictionary<Guid, MembershipTier> fallbackTierByUser,
            Instant now,
            CancellationToken ct = default);

    // ---- Consent-reminder storage command ----

    /// <summary>
    /// Sets <c>User.LastConsentReminderSentAt</c> to <paramref name="sentAt"/>.
    /// No-op if the user does not exist. Used by the re-consent reminder job
    /// so it does not write to the Users table directly (design-rules §2c).
    /// </summary>
    Task SetLastConsentReminderSentAsync(
        Guid userId, Instant sentAt, CancellationToken ct = default);

    // ---- Merge & admin cleanup commands ----

    /// <summary>
    /// Permanently deletes the requested user rows after the caller has cleared
    /// cross-section references. Removes the users' email rows through
    /// <c>IUserRepository</c> and removes external login rows through
    /// <c>IUserRepository</c>. Requires the current authenticated user to hold the full Admin role.
    /// </summary>
    Task<int> DeleteUsersAsync(
        IReadOnlyCollection<Guid> userIds,
        CancellationToken ct = default);

}

/// <summary>
/// Summary returned from <c>IAccountDeletionService.AnonymizeExpiredAccountAsync</c>
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
public record AnonymizedAccountSummary(
    string? OriginalEmail,
    string OriginalDisplayName,
    string PreferredLanguage);

