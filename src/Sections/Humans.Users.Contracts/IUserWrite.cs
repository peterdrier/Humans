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
    // ---- User storage commands ----

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
    /// Suspends or unsuspends: writes <c>users.State</c> only (unsuspending re-classifies from the
    /// remaining signals). <paramref name="adminSuspension"/> picks <see cref="UserState.AdminSuspended"/>
    /// over the consent <see cref="UserState.Suspended"/>. <c>NotFound</c> without a profile. Audit,
    /// notification and the reason belong to the caller.
    /// </summary>
    Task<OnboardingResult> SetSuspensionAsync(
        Guid userId, bool suspended, bool adminSuspension = false, CancellationToken ct = default);

    /// <summary>
    /// Records a signup rejection as the stored state (<see cref="UserState.Rejected"/>, which
    /// outranks everything but merge and deletion and is carried forward by every later
    /// re-classification). The reason, time and reviewer are Consent's review
    /// (<c>IConsentWrite.RecordRejectionAsync</c>). <c>NotFound</c> without a profile.
    /// </summary>
    Task<OnboardingResult> SetRejectedAsync(Guid userId, CancellationToken ct = default);

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
/// <param name="OriginalBurnerName">
/// The burner name on the account before anonymization.
/// </param>
/// <param name="PreferredLanguage">
/// The user's preferred language, used to render the confirmation email.
/// </param>
public record AnonymizedAccountSummary(
    string? OriginalEmail,
    string OriginalBurnerName,
    string PreferredLanguage);

