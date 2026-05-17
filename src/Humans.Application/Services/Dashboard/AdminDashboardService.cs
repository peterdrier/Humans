using Humans.Application.DTOs;
using Humans.Application.Interfaces.Dashboard;
using Humans.Application.Interfaces.Governance;
using Humans.Application.Interfaces.Users;

namespace Humans.Application.Services.Dashboard;

/// <summary>
/// Admin dashboard aggregator — partitions every user by membership state,
/// joins in tier-application stats, and computes a language distribution
/// across approved-not-suspended users for the dashboard's chart. Owns no
/// tables; all reads route through the owning section services
/// (<see cref="IUserService"/>, <see cref="IMembershipCalculator"/>,
/// <see cref="IApplicationDecisionService"/>). User identity, profile state,
/// and preferred-language reads come from the cached <see cref="UserInfo"/>
/// snapshot — every admin dashboard render previously did a per-render
/// <c>users</c> SELECT + GROUP BY for the language tile.
/// </summary>
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

        // Language distribution for the admin dashboard chart — approved,
        // non-suspended humans, grouped by PreferredLanguage. Union
        // Active + MissingConsents; pending-deletion users are not counted
        // (bucket is split off earlier by PartitionUsersAsync). Group in
        // memory over the cached UserInfo snapshot rather than a per-render
        // SQL GROUP BY — `UserInfo.PreferredLanguage` is already projected
        // from `User.PreferredLanguage` on the cache build. This is a
        // visualization, not an audit count.
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
