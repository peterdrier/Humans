using Humans.Base.Interfaces.Admin;
using Humans.Governance.Contracts;
using Humans.Shifts.Contracts;
using Humans.Tickets.Contracts;
using Humans.Users.Contracts;
using NodaTime;

namespace Humans.Users.Services;

/// <summary>Admin dashboard aggregator: membership partition, tier-application stats, language distribution, 4-set Venn/UpSet membership, audience segmentation. Owns no tables.</summary>
internal sealed class AdminDashboardService(
    IUserServiceRead userService,
    IMembershipCalculatorRead membershipCalculator,
    IApplicationServiceRead applicationDecisionService,
    IBurnSettingsService burnSettings,
    IShiftView shiftView,
    ITicketServiceRead ticketQueryService) : IAdminDashboardService
{
    public async Task<AdminDashboardData> GetAdminDashboardAsync(CancellationToken ct = default)
    {
        var snapshot = await userService.GetAllUserInfosAsync(ct).ConfigureAwait(false);
        var allUserIds = snapshot.Select(u => u.Id).ToList();
        var totalMembers = allUserIds.Count;
        var partition = await membershipCalculator.PartitionUsersAsync(allUserIds, ct);

        var pendingApplications =
            await applicationDecisionService.GetPendingApplicationCountAsync(ct);
        var appStats = await applicationDecisionService.GetAdminStatsAsync(ct);

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

    private async Task<UserSetMembership> BuildSetMembershipAsync(
        IReadOnlyCollection<UserInfo> snapshot,
        CancellationToken ct)
    {
        var activeEvent = await burnSettings.GetActiveAsync();
        var activeYear = activeEvent?.Year ?? 0;
        var shiftViews = await shiftView.GetUsersAsync(snapshot.Select(u => u.Id), ct);

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

    public async Task<AudienceSegmentation> GetAudienceSegmentationAsync(int? year, CancellationToken ct = default)
    {
        var allUsers = await userService.GetAllUserInfosAsync(ct).ConfigureAwait(false);
        var ticketOrders = await ticketQueryService.GetTicketOrdersAsync(ct);
        var start = year.HasValue ? Instant.FromUtc(year.Value, 1, 1, 0, 0) : default;
        var end = year.HasValue ? Instant.FromUtc(year.Value + 1, 1, 1, 0, 0) : default;
        IReadOnlySet<Guid> ticketUserIds = ticketOrders
            .Where(o => !year.HasValue || (o.PurchasedAt >= start && o.PurchasedAt < end))
            .SelectMany(o => o.MatchedUserId.HasValue
                ? o.Attendees
                    .Where(a => a.MatchedUserId.HasValue)
                    .Select(a => a.MatchedUserId!.Value)
                    .Append(o.MatchedUserId.Value)
                : o.Attendees
                    .Where(a => a.MatchedUserId.HasValue)
                    .Select(a => a.MatchedUserId!.Value))
            .ToHashSet();

        var withProfile = 0;
        var withTicket = 0;
        var withBoth = 0;
        var withNeither = 0;

        foreach (var user in allUsers)
        {
            var hasProfile = user.Profile is not null;
            var hasTicket = ticketUserIds.Contains(user.Id);

            if (hasProfile) withProfile++;
            if (hasTicket) withTicket++;
            if (hasProfile && hasTicket) withBoth++;
            if (!hasProfile && !hasTicket) withNeither++;
        }

        var years = ticketOrders
            .Where(o => o.MatchedUserId.HasValue)
            .Select(o => o.PurchasedAt.InUtc().Year)
            .Distinct()
            .OrderByDescending(y => y)
            .ToList();

        return new AudienceSegmentation(
            TotalAccounts: allUsers.Count,
            WithTicket: withTicket,
            WithProfile: withProfile,
            WithBoth: withBoth,
            WithNeither: withNeither,
            AvailableYears: years,
            SelectedYear: year);
    }
}
