using Humans.Application.DTOs;
using Humans.Application.Interfaces.Dashboard;
using Humans.Application.Interfaces.Governance;
using Humans.Application.Interfaces.Users;

namespace Humans.Application.Services.Dashboard;

/// <summary>Admin dashboard aggregator: membership partition, tier-application stats, language distribution. Owns no tables.</summary>
public sealed class AdminDashboardService : IAdminDashboardService
{
    private readonly IUserService _userService;
    private readonly IMembershipCalculator _membershipCalculator;
    private readonly IApplicationDecisionService _applicationDecisionService;

    public AdminDashboardService(
        IUserService userService,
        IMembershipCalculator membershipCalculator,
        IApplicationDecisionService applicationDecisionService)
    {
        _userService = userService;
        _membershipCalculator = membershipCalculator;
        _applicationDecisionService = applicationDecisionService;
    }

    public async Task<AdminDashboardData> GetAdminDashboardAsync(CancellationToken ct = default)
    {
        var snapshot = await _userService.GetAllUserInfosAsync(ct).ConfigureAwait(false);
        var allUserIds = snapshot.Select(u => u.Id).ToList();
        var totalMembers = allUserIds.Count;
        var partition = await _membershipCalculator.PartitionUsersAsync(allUserIds, ct);

        var pendingApplications =
            await _applicationDecisionService.GetPendingApplicationCountAsync(ct);
        var appStats = await _applicationDecisionService.GetAdminStatsAsync(ct);

        // Language distribution chart: Active ∪ MissingConsents (pending-deletion split off earlier).
        var approvedNotSuspended = new HashSet<Guid>(
            partition.Active.Concat(partition.MissingConsents));
        var languageDistribution = snapshot
            .Where(u => approvedNotSuspended.Contains(u.Id))
            .GroupBy(u => u.PreferredLanguage, StringComparer.Ordinal)
            .Select(g => new LanguageCount(g.Key, g.Count()))
            .OrderByDescending(l => l.Count)
            .ToList();

        return new AdminDashboardData(
            totalMembers,
            partition.IncompleteSignup.Count,
            partition.PendingApproval.Count,
            partition.Active.Count,
            partition.MissingConsents.Count,
            partition.Suspended.Count,
            partition.PendingDeletion.Count,
            pendingApplications,
            appStats.Total,
            appStats.Approved,
            appStats.Rejected,
            appStats.ColaboradorApplied,
            appStats.AsociadoApplied,
            languageDistribution);
    }

    public async Task<int> GetPendingReviewCountAsync(CancellationToken ct = default)
    {
        var count = (await _userService.GetAllUserInfosAsync(ct).ConfigureAwait(false)).Count(u => u.NeedsConsentReview);
        return count;
    }
}
