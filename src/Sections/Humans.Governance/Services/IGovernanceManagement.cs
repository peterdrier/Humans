using Humans.Base.Interfaces;
using Humans.Governance.Contracts;
using Humans.Governance.Services.Dtos;
using NodaTime;

namespace Humans.Governance.Services;

internal interface IGovernanceManagement : IApplicationService
{
    /// <summary>Newest first.</summary>
    Task<IReadOnlyList<UserApplicationSnapshot>> GetUserApplicationsAsync(
        Guid userId, CancellationToken ct = default);

    Task<int> GetUnvotedApplicationCountAsync(
        Guid boardMemberUserId, CancellationToken ct = default);

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
