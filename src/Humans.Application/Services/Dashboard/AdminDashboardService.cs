using Humans.Application.DTOs;
using Humans.Application.Interfaces.Dashboard;
using Humans.Application.Interfaces.Governance;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Users;

namespace Humans.Application.Services.Dashboard;

/// <summary>Admin dashboard aggregator: membership partition, tier-application stats, language distribution, 4-set Venn/UpSet membership. Owns no tables.</summary>
public sealed class AdminDashboardService : IAdminDashboardService
{
    private readonly IUserService _userService;
    private readonly IMembershipCalculator _membershipCalculator;
    private readonly IApplicationDecisionService _applicationDecisionService;
    private readonly IShiftManagementService _shiftManagement;
    private readonly IShiftView _shiftView;

    public AdminDashboardService(
        IUserService userService,
        IMembershipCalculator membershipCalculator,
        IApplicationDecisionService applicationDecisionService,
        IShiftManagementService shiftManagement,
        IShiftView shiftView)
    {
        _userService = userService;
        _membershipCalculator = membershipCalculator;
        _applicationDecisionService = applicationDecisionService;
        _shiftManagement = shiftManagement;
        _shiftView = shiftView;
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

        var setMembership = await BuildSetMembershipAsync(snapshot, ct);

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
            languageDistribution,
            setMembership);
    }

    public async Task<int> GetPendingReviewCountAsync(CancellationToken ct = default)
    {
        var count = (await _userService.GetAllUserInfosAsync(ct).ConfigureAwait(false)).Count(u => u.NeedsConsentReview);
        return count;
    }

    private async Task<UserSetMembership> BuildSetMembershipAsync(
        IReadOnlyCollection<UserInfo> snapshot,
        CancellationToken ct)
    {
        var activeEvent = await _shiftManagement.GetActiveAsync();
        var activeYear = activeEvent?.Year ?? 0;
        var shiftViews = await _shiftView.GetUsersAsync(snapshot.Select(u => u.Id), ct);

        var counts = new Dictionary<int, int>(capacity: 16);
        foreach (var u in snapshot)
        {
            var mask = 0;
            if (u.HasRequiredNameFields) mask |= UserSetMembership.ProfileBit;
            if (activeYear > 0 && u.HasTicketForYear(activeYear)) mask |= UserSetMembership.TicketBit;
            if (shiftViews[u.Id].HasShift) mask |= UserSetMembership.ShiftBit;
            // Explicit opt-in only: tri-state MarketingOptedOut == false (not null, not true).
            if (u.MarketingOptedOut == false) mask |= UserSetMembership.MarketingBit;

            counts[mask] = counts.GetValueOrDefault(mask) + 1;
        }

        return new UserSetMembership(counts);
    }
}
