using Humans.Users.Contracts;
using Humans.Base.Interfaces;
using NodaTime;

namespace Humans.Governance.Contracts;

/// <summary>
/// The write half of the tier-application lifecycle with a consumer outside the section:
/// Shell's <c>ProfileController</c> submit path. <c>TermRenewalReminderJob</c> drives the
/// renewal members from inside the section.
/// </summary>
/// <remarks>
/// Approve, reject, withdraw, board voting and the admin reads have no consumer outside the
/// section and stay on the internal <c>ApplicationDecisionService</c>, which the section's own
/// controllers inject by concrete type. Carve the leaf from the *call sites*, not from the
/// interface.
/// </remarks>
public interface IApplicationDecisionService : IApplicationServiceRead, IApplicationService
{
    /// <summary>
    /// The tier-application field rules, evaluated without touching storage: a non-Volunteer
    /// tier needs a motivation, and Asociado additionally needs a significant contribution and
    /// a role understanding. <see cref="SubmitAsync"/> runs this first, so the two can't drift.
    /// <para>
    /// Exposed separately so a caller that must decide <em>before</em> writing anything — the
    /// profile edit form, which validates the whole submit up front so a bad post can't
    /// half-save — gets the same answer the submit would give. Returns the same
    /// <c>ErrorKey</c>s (<c>InvalidTier</c>, <c>MotivationRequired</c>,
    /// <c>SignificantContributionRequired</c>, <c>RoleUnderstandingRequired</c>) for callers
    /// to map onto their own localized, field-targeted messages.
    /// </para>
    /// </summary>
    ApplicationDecisionResult ValidateSubmission(
        MembershipTier tier, string? motivation,
        string? significantContribution, string? roleUnderstanding);

    Task<ApplicationDecisionResult> SubmitAsync(
        Guid userId, MembershipTier tier, string motivation,
        string? additionalInfo, string? significantContribution, string? roleUnderstanding,
        string language, CancellationToken ct = default);

    /// <summary>
    /// Only allowed on <see cref="ApplicationStatus.Submitted"/> applications.
    /// </summary>
    Task UpdateDraftApplicationAsync(
        Guid applicationId, MembershipTier tier, string motivation,
        string? additionalInfo, string? significantContribution, string? roleUnderstanding,
        CancellationToken ct = default);

    /// <summary>
    /// Returns every <see cref="ApplicationStatus.Approved"/> application
    /// whose <c>TermExpiresAt</c> falls between <paramref name="today"/>
    /// (inclusive) and <paramref name="reminderThreshold"/> (inclusive) and
    /// whose <c>RenewalReminderSentAt</c> is still null. Used by the term
    /// renewal reminder job so it can enumerate candidates without reading
    /// <c>applications</c> directly (design-rules §2c).
    /// </summary>
    Task<IReadOnlyList<ApplicationRenewalReminderCandidate>> GetExpiringApplicationsNeedingReminderAsync(
        LocalDate today, LocalDate reminderThreshold, CancellationToken ct = default);

    /// <summary>
    /// Lets the renewal job exclude users who have already filed a renewal.
    /// </summary>
    Task<IReadOnlySet<(Guid UserId, MembershipTier Tier)>> GetPendingApplicationUserTiersAsync(
        CancellationToken ct = default);

    /// <summary>
    /// No-op if the application does not exist.
    /// </summary>
    Task MarkRenewalReminderSentAsync(
        Guid applicationId, Instant sentAt, CancellationToken ct = default);
}

public record ApplicationDecisionResult(bool Success, string? ErrorKey = null, Guid? ApplicationId = null);

public sealed record UserApplicationSnapshot(
    Guid Id,
    Guid UserId,
    ApplicationStatus Status,
    MembershipTier MembershipTier,
    Instant SubmittedAt,
    Instant? ResolvedAt,
    LocalDate? TermExpiresAt,
    string Motivation,
    string? AdditionalInfo,
    string? SignificantContribution,
    string? RoleUnderstanding);

public sealed record ApplicationRenewalReminderCandidate(
    Guid Id,
    Guid UserId,
    MembershipTier MembershipTier,
    Instant SubmittedAt,
    LocalDate? TermExpiresAt);

public sealed record SubmittedApplicationSnapshot(
    Guid Id,
    MembershipTier MembershipTier,
    string Motivation);
