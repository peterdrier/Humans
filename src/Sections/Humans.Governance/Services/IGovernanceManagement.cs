using Humans.Base.Interfaces;
using Humans.Governance.Contracts;
using Humans.Users.Contracts;
using NodaTime;

namespace Humans.Governance.Services;

/// <summary>
/// Full Governance tier-application service surface. Other sections depend on
/// <see cref="IGovernanceService"/> for reads; this adds the tier term expiry, the
/// renewal-reminder members and admin reads used inside the section.
/// </summary>
internal interface IGovernanceManagement : IGovernanceService, IApplicationService
{
    /// <summary>
    /// Term expiry for <paramref name="tier"/>: every member holding it without an Approved
    /// application for it still in term on <paramref name="today"/> drops to their other in-term
    /// tier, or to Volunteer. Returns each member with the tier written, for the caller's
    /// <c>TierDowngraded</c> audit entries. Run by <c>MembershipTierExpiryJob</c>.
    /// </summary>
    Task<IReadOnlyList<(Guid UserId, MembershipTier NewTier)>> ExpireMembershipTiersAsync(
        MembershipTier tier, LocalDate today, CancellationToken ct = default);

    /// <summary>
    /// Returns the latest <see cref="ApplicationStatus.Approved"/> application per user and tier
    /// when its <c>TermExpiresAt</c> falls between <paramref name="today"/>
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

    Task<ApplicationAdminStats> GetAdminStatsAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the count of pending (Submitted) applications. Used by the
    /// admin dashboard.
    /// </summary>
    Task<int> GetPendingApplicationCountAsync(CancellationToken ct = default);
}
