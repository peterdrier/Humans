using Humans.Onboarding.Contracts;

namespace Humans.Users.Contracts;

/// <summary>The section's own writes are on the internal <c>IUserManagement</c>, which inherits this.</summary>
public interface IUserWrite
{
    /// <summary>No-op if the user doesn't exist.</summary>
    Task SetPreferredLanguageAsync(Guid userId, string preferredLanguage, CancellationToken ct = default);

    /// <summary>
    /// After any successful sign-in. The only writer of <c>LastLoginAt</c>: sign-in flows must not set it directly.
    /// Tracking failures are logged without blocking sign-in; caller cancellation propagates.
    /// No-op if the user doesn't exist.
    /// </summary>
    Task RecordLoginAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Marks the account as not a real person, so people-only queues and counts skip it. Idempotent: audited only
    /// on the first mark. No-op if the user doesn't exist.
    /// </summary>
    Task MarkFakeAsync(Guid userId, Guid actorUserId, string reason, CancellationToken ct = default);

    /// <summary>
    /// Sets <see cref="UserState.Rejected"/>, which outranks everything but merge and deletion and survives every
    /// later re-classification. The reason and reviewer are Consent's. <c>NotFound</c> without a profile.
    /// </summary>
    Task<OnboardingResult> SetRejectedAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Permanent. The caller has already cleared cross-section references. Requires the current user to hold the
    /// full Admin role.
    /// </summary>
    Task<int> DeleteUsersAsync(
        IReadOnlyCollection<Guid> userIds,
        CancellationToken ct = default);

}

/// <summary>The pre-anonymization values the confirmation email and audit need.</summary>
/// <param name="OriginalEmail">Null when the account never had an email.</param>
/// <param name="OriginalBurnerName">Before anonymization.</param>
/// <param name="PreferredLanguage">For the confirmation email.</param>
public record AnonymizedAccountSummary(
    string? OriginalEmail,
    string OriginalBurnerName,
    string PreferredLanguage);

