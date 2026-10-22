using Humans.Base.Interfaces;
using Humans.Governance.Contracts;
using NodaTime;

namespace Humans.Governance.Services;

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
    /// The latest Approved application per user and tier whose term ends between
    /// <paramref name="today"/> and <paramref name="reminderThreshold"/> (inclusive), not yet reminded.
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

    /// <summary>Count of Submitted applications.</summary>
    Task<int> GetPendingApplicationCountAsync(CancellationToken ct = default);
}
