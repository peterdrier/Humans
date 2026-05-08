using Humans.Application.DTOs;
using Humans.Application.Interfaces.Dashboard;
using Humans.Application.Interfaces.Governance;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Users;

namespace Humans.Application.Services.Dashboard;

/// <summary>
/// Admin dashboard aggregator — partitions every user by membership state,
/// joins in tier-application stats, and computes a language distribution
/// across approved-not-suspended users for the dashboard's chart. Owns no
/// tables; all reads route through the owning section services
/// (<see cref="IUserService"/>, <see cref="IProfileService"/>,
/// <see cref="IMembershipCalculator"/>, <see cref="IApplicationDecisionService"/>).
/// </summary>
public sealed class AdminDashboardService : IAdminDashboardService
{
    private readonly IUserService _userService;
    private readonly IProfileService _profileService;
    private readonly IMembershipCalculator _membershipCalculator;
    private readonly IApplicationDecisionService _applicationDecisionService;

    public AdminDashboardService(
        IUserService userService,
        IProfileService profileService,
        IMembershipCalculator membershipCalculator,
        IApplicationDecisionService applicationDecisionService)
    {
        _userService = userService;
        _profileService = profileService;
        _membershipCalculator = membershipCalculator;
        _applicationDecisionService = applicationDecisionService;
    }

    public async Task<AdminDashboardData> GetAdminDashboardAsync(CancellationToken ct = default)
    {
        var allUsers = await _userService.GetAllUsersAsync(ct);
        var allUserIds = allUsers.Select(u => u.Id).ToList();
        var totalMembers = allUserIds.Count;
        var partition = await _membershipCalculator.PartitionUsersAsync(allUserIds, ct);

        var pendingApplications =
            await _applicationDecisionService.GetPendingApplicationCountAsync(ct);
        var appStats = await _applicationDecisionService.GetAdminStatsAsync(ct);

        // Language distribution for the admin dashboard chart — approved,
        // non-suspended humans, grouped by PreferredLanguage. Union
        // Active + MissingConsents; pending-deletion users are not counted
        // (bucket is split off earlier by PartitionUsersAsync). This is a
        // visualization, not an audit count, so sub-user-count drift from
        // the pre-partition predicate is acceptable. Pass to the User
        // section, which owns preferred language — no cross-domain join
        // (design-rules §6).
        var approvedNotSuspended = partition.Active
            .Concat(partition.MissingConsents)
            .ToList();
        var rawLanguageDistribution = await _userService.GetLanguageDistributionForUserIdsAsync(
            approvedNotSuspended, ct);
        var languageDistribution = rawLanguageDistribution
            .Select(x => new LanguageCount(x.Language, x.Count))
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

    public Task<int> GetPendingReviewCountAsync(CancellationToken ct = default) =>
        _profileService.GetPendingReviewCountAsync(ct);
}
