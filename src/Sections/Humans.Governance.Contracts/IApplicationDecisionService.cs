using Humans.Users.Contracts;
using Humans.Application.Interfaces;
using NodaTime;

namespace Humans.Governance.Contracts;

/// <summary>
/// The write half of the tier-application lifecycle that lives outside the section: the
/// profile-setup submit path (Shell's <c>ProfileController</c>, which renders the same
/// application form during onboarding). The term-renewal reminder job also drives the three
/// renewal members, but is no longer outside the section: <c>TermRenewalReminderJob</c> moved
/// from <c>Humans.Infrastructure/Jobs</c> into <c>Humans.Governance/Contracts/</c> at G5
/// lane 5b-4 (nobodies-collective/Humans#866).
/// </summary>
/// <remarks>
/// The rest of the lifecycle — approve, reject, withdraw, board voting, the admin list and
/// detail reads — has no consumer outside the section and stays on the internal
/// <c>ApplicationDecisionService</c>, which the section's own controllers inject by concrete
/// type (design §15 step 5). Carve the leaf from the *call sites*, not from the interface.
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
    /// Updates a submitted (draft) application's motivation, tier, and optional
    /// Asociado fields. Only allowed on <see cref="ApplicationStatus.Submitted"/>
    /// applications. Used by the profile save flow during initial setup.
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
    /// Returns the distinct (UserId, MembershipTier) pairs that currently
    /// have a <see cref="ApplicationStatus.Submitted"/> application. Used by
    /// the term renewal reminder job to exclude users who have already filed
    /// a renewal.
    /// </summary>
    Task<IReadOnlySet<(Guid UserId, MembershipTier Tier)>> GetPendingApplicationUserTiersAsync(
        CancellationToken ct = default);

    /// <summary>
    /// Stamps <c>Application.RenewalReminderSentAt</c> to
    /// <paramref name="sentAt"/>. No-op if the application does not exist.
    /// Used by the term renewal reminder job so it never writes to
    /// <c>applications</c> directly.
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
