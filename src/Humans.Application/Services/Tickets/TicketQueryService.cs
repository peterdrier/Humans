using System.Diagnostics.CodeAnalysis;
using NodaTime;
using Humans.Application;
using Humans.Application.DTOs;
using Humans.Application.Extensions;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Constants;
using Humans.Domain.Enums;
using Humans.Application.Interfaces.Budget;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Campaigns;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Tickets;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;

namespace Humans.Application.Services.Tickets;

/// <summary>
/// Tickets read service (inner) behind <c>CachingTicketQueryService</c> decorator (§15 / T-07).
/// </summary>
public sealed class TicketQueryService(
    ITicketRepository ticketRepository,
    IBudgetService budgetService,
    ICampaignService campaignService,
    IUserService userService,
    IUserEmailService userEmailService,
    ITeamServiceRead teamService,
    IShiftManagementService shiftManagementService,
    IClock clock) : ITicketService, IUserDataContributor
{
    private async Task<int> ComputeUserTicketCountAsync(Guid userId)
    {
        // Attendees only — buyers don't count for themselves.
        var matchedCount = await ticketRepository
            .CountValidAttendeesMatchedToUserAsync(userId);
        if (matchedCount > 0)
            return matchedCount;

        // Fallback: verified-emails ↔ attendee-emails, compared in-memory for consistent casing.
        var verifiedEmails = await userEmailService.GetVerifiedEmailsForUserAsync(userId);
        if (verifiedEmails.Count == 0)
            return 0;

        var attendeeEmails = await ticketRepository.GetValidAttendeeEmailsAsync();
        if (attendeeEmails.Count == 0)
            return 0;

        var verifiedSet = verifiedEmails.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return attendeeEmails.Count(verifiedSet.Contains);
    }

    public async Task<IReadOnlyList<TicketOrderInfo>> GetTicketOrdersAsync(CancellationToken ct = default)
    {
        var syncState = await ticketRepository.GetSyncStateAsync(ct);
        var currentEventId = syncState?.VendorEventId;
        var orders = await ticketRepository.GetAllOrdersWithAttendeesAsync(ct);

        return orders
            .Select(o => Project(o, currentEventId))
            .ToList();
    }

    private async Task<HashSet<Guid>> GetUserIdsWithTicketsAsync()
    {
        var syncState = await ticketRepository.GetSyncStateAsync();
        if (syncState is null || string.IsNullOrEmpty(syncState.VendorEventId))
            return [];

        var ids = await ticketRepository.GetValidMatchedAttendeeUserIdsForEventAsync(syncState.VendorEventId);
        return ids.ToHashSet();
    }

    public async Task<List<string>> GetAvailableTicketTypesAsync()
    {
        var types = await ticketRepository.GetDistinctTicketTypesAsync();
        return types.ToList();
    }

    private async Task<HashSet<Guid>> GetAllMatchedUserIdsAsync()
    {
        var fromAttendees = await ticketRepository.GetAllMatchedAttendeeUserIdsAsync();
        var fromOrders = await ticketRepository.GetAllMatchedOrderUserIdsAsync();
        return fromAttendees.Concat(fromOrders).ToHashSet();
    }

    public async Task<TicketDashboardStats> GetDashboardStatsAsync()
    {
        // Crash recovery: reset Running >30min.
        var now = clock.GetCurrentInstant();
        var staleThreshold = now - Duration.FromMinutes(30);
        var syncState = await ticketRepository.ResetStaleRunningStateAsync(
            olderThan: staleThreshold,
            now: now,
            errorMessage: "Sync state was stuck in Running for >30 minutes (likely crash). Auto-reset.");

        var totals = await ticketRepository.GetDashboardTotalsAsync();

        var revenue = totals.GrossRevenue;
        var totalStripeFees = totals.TotalStripeFees;
        var totalAppFees = totals.TotalApplicationFees;
        var ticketsSold = totals.TicketsSold;
        var netRevenue = revenue - totalStripeFees - totalAppFees;
        var avgPrice = ticketsSold > 0 ? netRevenue / ticketsSold : 0;
        var grossAvgPrice = ticketsSold > 0 ? revenue / ticketsSold : 0;
        var unmatchedCount = totals.UnmatchedOrderCount;

        var feeData = await ticketRepository.GetPaidOrderPaymentMethodsAsync();

        var feesByMethod = feeData
            .GroupBy(
                o => o.PaymentMethodDetail != null ? $"{o.PaymentMethod}/{o.PaymentMethodDetail}" : o.PaymentMethod!,
                StringComparer.Ordinal)
            .Select(g =>
            {
                var totalAmt = g.Sum(o => o.TotalAmount);
                var totalStripe = g.Sum(o => o.StripeFee ?? 0m);
                return new FeeBreakdownByMethod
                {
                    PaymentMethod = g.Key,
                    OrderCount = g.Count(),
                    TotalAmount = totalAmt,
                    TotalStripeFees = totalStripe,
                    TotalApplicationFees = g.Sum(o => o.ApplicationFee ?? 0m),
                    EffectiveRate = totalAmt > 0 ? Math.Round(totalStripe / totalAmt * 100, 2) : 0,
                };
            })
            .OrderByDescending(f => f.TotalStripeFees)
            .ToList();

        var orderDates = await ticketRepository.GetOrderDateAttendeeCountsAsync();

        var salesByDate = orderDates
            .GroupBy(o => o.PurchasedAt.InUtc().Date)
            .ToDictionary(g => g.Key, g => g.Sum(o => o.AttendeeCount));

        var dailySalesPoints = new List<DailySales>();
        if (salesByDate.Count > 0)
        {
            var startDate = salesByDate.Keys.Min();
            var endDate = salesByDate.Keys.Max();
            var allDays = new List<(LocalDate Date, int Count)>();

            for (var d = startDate; d <= endDate; d = d.PlusDays(1))
                allDays.Add((d, salesByDate.GetValueOrDefault(d, 0)));

            for (var i = 0; i < allDays.Count; i++)
            {
                var (date, count) = allDays[i];
                var windowStart = Math.Max(0, i - 6);
                var window = allDays.Skip(windowStart).Take(i - windowStart + 1);
                var rollingAvg = window.Average(d => (decimal)d.Count);

                dailySalesPoints.Add(new DailySales
                {
                    Date = date.ToIsoDateString(),
                    TicketsSold = count,
                    RollingAverage = Math.Round(rollingAvg, 1),
                });
            }
        }

        var recentOrders = await ticketRepository.GetRecentOrdersAsync(count: 10);

        var volunteerTeam = await teamService.GetTeamAsync(SystemTeamIds.Volunteers);
        var volunteerUserIds = volunteerTeam?.Members.Select(m => m.UserId).ToList() ?? [];
        var totalActiveVolunteers = volunteerUserIds.Count;

        var userIdsWithTickets = await GetUserIdsWithTicketsAsync();
        var volunteersWithTickets = volunteerUserIds.Count(userIdsWithTickets.Contains);

        var volunteerCoveragePct = totalActiveVolunteers > 0
            ? Math.Round(volunteersWithTickets * 100m / totalActiveVolunteers, 1)
            : 0;

        return new TicketDashboardStats
        {
            TicketsSold = ticketsSold,
            Revenue = revenue,
            TotalStripeFees = totalStripeFees,
            TotalApplicationFees = totalAppFees,
            NetRevenue = netRevenue,
            AveragePrice = avgPrice,
            GrossAveragePrice = grossAvgPrice,
            UnmatchedOrderCount = unmatchedCount,
            FeesByPaymentMethod = feesByMethod,
            DailySalesPoints = dailySalesPoints,
            RecentOrders = recentOrders.ToList(),
            SyncStatus = syncState?.SyncStatus ?? TicketSyncStatus.Idle,
            SyncError = syncState?.LastError,
            LastSyncAt = syncState?.LastSyncAt,
            TotalActiveVolunteers = totalActiveVolunteers,
            VolunteersWithTickets = volunteersWithTickets,
            VolunteerCoveragePercent = volunteerCoveragePct,
        };
    }

    public async Task<BreakEvenResult> CalculateBreakEvenAsync(
        int ticketsSold,
        decimal grossRevenue,
        string currency,
        bool canAccessFinance,
        int fallbackTarget)
    {
        if (ticketsSold <= 0 || grossRevenue <= 0)
            return new BreakEvenResult { Target = fallbackTarget, Currency = currency };

        var activeBudgetYear = await budgetService.GetActiveYearAsync();
        if (activeBudgetYear is null)
            return new BreakEvenResult { Target = fallbackTarget, Currency = currency };

        var summary = budgetService.ComputeBudgetSummary(activeBudgetYear.Groups);
        var plannedExpenses = Math.Abs(summary.TotalExpenses);
        if (plannedExpenses <= 0)
            return new BreakEvenResult { Target = fallbackTarget, Currency = currency };

        // break-even = sold + ceil((plannedExpenses - revenue) / avgPrice)
        var grossAverageTicketPrice = grossRevenue / ticketsSold;
        var remainingExpenses = Math.Max(0m, plannedExpenses - grossRevenue);
        long remainingTicketsToSell = 0;
        if (remainingExpenses > 0)
        {
            var remainingTicketCount = Math.Ceiling(remainingExpenses / grossAverageTicketPrice);
            remainingTicketsToSell = remainingTicketCount > int.MaxValue
                ? int.MaxValue
                : decimal.ToInt32(remainingTicketCount);
        }

        var breakEvenTarget = ticketsSold + remainingTicketsToSell;
        var target = breakEvenTarget > int.MaxValue ? int.MaxValue : (int)breakEvenTarget;

        var detail = canAccessFinance
            ? $"{currency} {remainingExpenses:N2} remaining expenses / {currency} {grossAverageTicketPrice:N2} gross avg. per ticket = {remainingTicketsToSell:N0} tickets still to sell"
            : null;

        return new BreakEvenResult { Target = target, Detail = detail, Currency = currency };
    }

    public async Task<CodeTrackingData> GetCodeTrackingDataAsync(string? search)
    {
        var campaignData = await campaignService.GetCodeTrackingAsync();

        var campaignSummaries = campaignData.Campaigns
            .Select(c => new CampaignCodeSummaryDto
            {
                CampaignId = c.CampaignId,
                CampaignTitle = c.CampaignTitle,
                TotalGrants = c.TotalGrants,
                Redeemed = c.Redeemed,
                Unused = c.TotalGrants - c.Redeemed,
                RedemptionRate = c.TotalGrants > 0
                    ? Math.Round(c.Redeemed * 100m / c.TotalGrants, 1)
                    : 0,
            })
            .ToList();

        IEnumerable<CampaignCodeTrackingGrant> allGrants = campaignData.Grants;
        if (!string.IsNullOrWhiteSpace(search) && search.Trim().Length >= 1)
        {
            allGrants = allGrants.Where(g =>
                (g.Code?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                g.RecipientName.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        var ordersWithCodes = await ticketRepository.GetOrdersWithDiscountCodesAsync();
        var orderByCode = ordersWithCodes
            .GroupBy(o => o.DiscountCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var codeRows = allGrants.Select(g =>
        {
            var code = g.Code;
            orderByCode.TryGetValue(code ?? string.Empty, out var matchedOrder);
            return new CodeDetailDto
            {
                Code = code ?? "—",
                RecipientName = g.RecipientName,
                RecipientUserId = g.UserId,
                CampaignTitle = g.CampaignTitle,
                Status = g.RedeemedAt is not null ? "Redeemed" : (g.LatestEmailStatus ?? "Pending"),
                RedeemedAt = g.RedeemedAt,
                RedeemedByName = matchedOrder?.BuyerName,
                RedeemedByEmail = matchedOrder?.BuyerEmail,
                RedeemedOrderVendorId = matchedOrder?.VendorOrderId,
            };
        }).ToList();

        var totalSent = campaignSummaries.Sum(c => c.TotalGrants);
        var totalRedeemed = campaignSummaries.Sum(c => c.Redeemed);

        return new CodeTrackingData
        {
            TotalCodesSent = totalSent,
            CodesRedeemed = totalRedeemed,
            CodesUnused = totalSent - totalRedeemed,
            RedemptionRate = totalSent > 0 ? Math.Round(totalRedeemed * 100m / totalSent, 1) : 0,
            Campaigns = campaignSummaries,
            Codes = codeRows,
        };
    }

    public async Task<TicketSalesAggregates> GetSalesAggregatesAsync()
    {
        var orders = await ticketRepository.GetPaidOrderSalesRowsAsync();

        var weeklySales = orders
            .GroupBy(o =>
            {
                var date = o.PurchasedAt.InUtc().Date;
                var monday = date.PlusDays(-(int)date.DayOfWeek + 1);
                return monday;
            })
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var monday = g.Key;
                var sunday = monday.PlusDays(6);
                return new WeeklySalesAggregate
                {
                    WeekLabel = $"{monday.ToString("MMM d", null)} – {sunday.ToString("MMM d", null)}",
                    TicketsSold = g.Sum(o => o.AttendeeCount),
                    GrossRevenue = g.Sum(o => o.TotalAmount),
                    OrderCount = g.Count(),
                    Donations = g.Sum(o => o.DonationAmount),
                    VatAmount = g.Sum(o => o.VatAmount),
                    VipDonations = g.Sum(o => o.VipDonations),
                };
            })
            .ToList();

        var quarterlySales = orders
            .GroupBy(o =>
            {
                var date = o.PurchasedAt.InUtc().Date;
                var quarter = (date.Month - 1) / 3 + 1;
                return (date.Year, quarter);
            })
            .OrderBy(g => g.Key.Year).ThenBy(g => g.Key.quarter)
            .Select(g => new QuarterlySalesAggregate
            {
                QuarterLabel = $"Q{g.Key.quarter} {g.Key.Year}",
                Year = g.Key.Year,
                Quarter = g.Key.quarter,
                TicketsSold = g.Sum(o => o.AttendeeCount),
                GrossRevenue = g.Sum(o => o.TotalAmount),
                OrderCount = g.Count(),
                Donations = g.Sum(o => o.DonationAmount),
                VatAmount = g.Sum(o => o.VatAmount),
                VipDonations = g.Sum(o => o.VipDonations),
            })
            .ToList();

        return new TicketSalesAggregates
        {
            WeeklySales = weeklySales,
            QuarterlySales = quarterlySales,
        };
    }

    public async Task<OrdersPageResult> GetOrdersPageAsync(
        string? search, string sortBy, bool sortDesc,
        int page, int pageSize,
        string? filterPaymentStatus, string? filterTicketType, bool? filterMatched)
    {
        TicketPaymentStatus? parsedStatus = null;
        if (!string.IsNullOrEmpty(filterPaymentStatus) &&
            Enum.TryParse<TicketPaymentStatus>(filterPaymentStatus, true, out var paymentStatus))
        {
            parsedStatus = paymentStatus;
        }

        var (rawRows, totalCount) = await ticketRepository.GetOrdersPageAsync(
            search, sortBy, sortDesc, page, pageSize,
            parsedStatus, filterTicketType, filterMatched);

        var rows = await HydrateOrderMatchedUserNamesAsync(rawRows);
        return new OrdersPageResult { Rows = rows, TotalCount = totalCount };
    }

    public async Task<AttendeesPageResult> GetAttendeesPageAsync(
        string? search, string sortBy, bool sortDesc,
        int page, int pageSize,
        string? filterTicketType, string? filterStatus, bool? filterMatched, string? filterOrderId,
        bool filterMultipleTickets = false)
    {
        TicketAttendeeStatus? parsedStatus = null;
        if (!string.IsNullOrEmpty(filterStatus) &&
            Enum.TryParse<TicketAttendeeStatus>(filterStatus, true, out var status))
        {
            parsedStatus = status;
        }

        var (rawRows, totalCount) = await ticketRepository.GetAttendeesPageAsync(
            search, sortBy, sortDesc, page, pageSize,
            filterTicketType, parsedStatus, filterMatched, filterOrderId, filterMultipleTickets);

        var rows = await HydrateAttendeeMatchedUserNamesAsync(rawRows);
        return new AttendeesPageResult { Rows = rows, TotalCount = totalCount };
    }

    private async Task<List<OrderRow>> HydrateOrderMatchedUserNamesAsync(IReadOnlyList<OrderRow> rows)
    {
        var matchedIds = rows
            .Where(r => r.MatchedUserId is not null)
            .Select(r => r.MatchedUserId!.Value)
            .Distinct()
            .ToList();
        if (matchedIds.Count == 0)
            return rows.ToList();

        var users = await userService.GetUserInfosAsync(matchedIds);
        return rows.Select(r =>
        {
            if (r.MatchedUserId is { } uid && users.TryGetValue(uid, out var user))
            {
                return new OrderRow
                {
                    Id = r.Id,
                    VendorOrderId = r.VendorOrderId,
                    PurchasedAt = r.PurchasedAt,
                    BuyerName = r.BuyerName,
                    BuyerEmail = r.BuyerEmail,
                    AttendeeCount = r.AttendeeCount,
                    TotalAmount = r.TotalAmount,
                    Currency = r.Currency,
                    DiscountCode = r.DiscountCode,
                    DiscountAmount = r.DiscountAmount,
                    DonationAmount = r.DonationAmount,
                    VatAmount = r.VatAmount,
                    PaymentMethod = r.PaymentMethod,
                    PaymentMethodDetail = r.PaymentMethodDetail,
                    StripeFee = r.StripeFee,
                    ApplicationFee = r.ApplicationFee,
                    PaymentStatus = r.PaymentStatus,
                    VendorDashboardUrl = r.VendorDashboardUrl,
                    MatchedUserId = r.MatchedUserId,
                    MatchedUserName = user.BurnerName,
                };
            }
            return r;
        }).ToList();
    }

    private async Task<List<AttendeeRow>> HydrateAttendeeMatchedUserNamesAsync(IReadOnlyList<AttendeeRow> rows)
    {
        var matchedIds = rows
            .Where(r => r.MatchedUserId is not null)
            .Select(r => r.MatchedUserId!.Value)
            .Distinct()
            .ToList();
        if (matchedIds.Count == 0)
            return rows.ToList();

        var users = await userService.GetUserInfosAsync(matchedIds);
        return rows.Select(r =>
        {
            if (r.MatchedUserId is { } uid && users.TryGetValue(uid, out var user))
            {
                return new AttendeeRow
                {
                    Id = r.Id,
                    AttendeeName = r.AttendeeName,
                    AttendeeEmail = r.AttendeeEmail,
                    TicketTypeName = r.TicketTypeName,
                    Price = r.Price,
                    Status = r.Status,
                    MatchedUserId = r.MatchedUserId,
                    MatchedUserName = user.BurnerName,
                    VendorOrderId = r.VendorOrderId,
                };
            }
            return r;
        }).ToList();
    }

    public async Task<WhoHasntBoughtResult> GetWhoHasntBoughtAsync(
        string? search, string? filterTeam, string? filterTier, string? filterTicketStatus,
        int page, int pageSize)
    {
        var matchedUserIds = await GetAllMatchedUserIdsAsync();

        var allUsers = await userService.GetAllUserInfosAsync().ConfigureAwait(false);
        var volunteerTeam = await teamService.GetTeamAsync(SystemTeamIds.Volunteers);
        var volunteerUserIds = volunteerTeam?.Members.Select(m => m.UserId).ToHashSet() ?? [];

        var candidateIds = allUsers
            .Where(u => volunteerUserIds.Contains(u.Id))
            .Select(u => u.Id)
            .ToList();

        var activeEvent = await shiftManagementService.GetActiveAsync();
        HashSet<Guid> notAttendingSet = [];
        if (activeEvent is not null && activeEvent.Year > 0)
        {
            var participations = await userService.GetAllParticipationsForYearAsync(activeEvent.Year);
            notAttendingSet = participations
                .Where(ep => ep.Status == ParticipationStatus.NotAttending)
                .Select(ep => ep.UserId)
                .ToHashSet();
        }

        candidateIds = candidateIds.Where(id => !notAttendingSet.Contains(id)).ToList();
        if (candidateIds.Count == 0)
        {
            return new WhoHasntBoughtResult
            {
                Humans = [],
                TotalCount = 0,
                AvailableTeams = [],
            };
        }

        var emailsById = await userEmailService.GetNotificationEmailsByUserIdsAsync(candidateIds);
        var candidateIdSet = candidateIds.ToHashSet();
        var teamsByIdLookup = await teamService.GetTeamsAsync();
        var teamsByUser = new Dictionary<Guid, IReadOnlyList<string>>();
        foreach (var team in teamsByIdLookup.Values
            .Where(t => t.IsActive && t.SystemTeamType == SystemTeamType.None && !t.IsHidden))
        {
            foreach (var member in team.Members.Where(m => candidateIdSet.Contains(m.UserId)))
            {
                if (!teamsByUser.TryGetValue(member.UserId, out var names))
                {
                    names = new List<string>();
                    teamsByUser[member.UserId] = names;
                }
                ((List<string>)names).Add(team.Name);
            }
        }

        var usersById = allUsers.ToDictionary(u => u.Id);

        var rows = candidateIds
            .Where(id => usersById.TryGetValue(id, out var u) && u.Profile is not null)
            .Select(id =>
            {
                var user = usersById[id];
                emailsById.TryGetValue(id, out var email);
                teamsByUser.TryGetValue(id, out var teamNames);

                return new WhoHasntBoughtRow
                {
                    UserId = id,
                    HasTicket = matchedUserIds.Contains(id),
                    Name = user.BurnerName,
                    Email = email ?? string.Empty,
                    TeamNames = teamNames ?? [],
                    Tier = user.Profile!.MembershipTier,
                };
            })
            .ToList();

        // Search the full verified-email set so admins can find secondary-address purchases.
        HashSet<Guid>? emailMatchUserIds = null;
        if (HasSearchTerm(search, 1))
        {
            var matches = await userEmailService.SearchUserIdsByVerifiedEmailAsync(search!);
            emailMatchUserIds = matches.ToHashSet();
        }

        var filtered = FilterWhoHasntBoughtRows(
            rows, filterTicketStatus, filterTeam, filterTier, search, emailMatchUserIds);

        var totalCount = filtered.Count;
        var pagedHumans = filtered
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(r => new WhoHasntBoughtRowDto
            {
                UserId = r.UserId,
                HasTicket = r.HasTicket,
                Name = r.Name,
                Email = r.Email,
                Teams = string.Join(", ", r.TeamNames),
                Tier = r.Tier,
            })
            .ToList();

        var availableTeams = rows
            .SelectMany(r => r.TeamNames)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new WhoHasntBoughtResult
        {
            Humans = pagedHumans,
            TotalCount = totalCount,
            AvailableTeams = availableTeams,
        };
    }

    private sealed class WhoHasntBoughtRow
    {
        public required Guid UserId { get; init; }
        public required bool HasTicket { get; init; }
        public required string Name { get; init; }
        public required string Email { get; init; }
        public required IReadOnlyList<string> TeamNames { get; init; }
        public required MembershipTier Tier { get; init; }
    }

    private static List<WhoHasntBoughtRow> FilterWhoHasntBoughtRows(
        IEnumerable<WhoHasntBoughtRow> rows,
        string? filterTicketStatus,
        string? filterTeam,
        string? filterTier,
        string? search,
        IReadOnlySet<Guid>? emailMatchUserIds = null)
    {
        IEnumerable<WhoHasntBoughtRow> filtered = rows;

        if (string.Equals(filterTicketStatus, "bought", StringComparison.OrdinalIgnoreCase))
            filtered = filtered.Where(r => r.HasTicket);
        else if (string.Equals(filterTicketStatus, "not_bought", StringComparison.OrdinalIgnoreCase))
            filtered = filtered.Where(r => !r.HasTicket);

        if (!string.IsNullOrEmpty(filterTeam))
            filtered = filtered.Where(r =>
                r.TeamNames.Any(tn => string.Equals(tn, filterTeam, StringComparison.OrdinalIgnoreCase)));

        if (!string.IsNullOrEmpty(filterTier) &&
            Enum.TryParse<MembershipTier>(filterTier, ignoreCase: true, out var parsedTier))
        {
            filtered = filtered.Where(r => r.Tier == parsedTier);
        }

        if (HasSearchTerm(search, 1))
        {
            filtered = filtered.Where(r =>
                ContainsIgnoreCase(r.Name, search) ||
                ContainsIgnoreCase(r.Email, search) ||
                (emailMatchUserIds is not null && emailMatchUserIds.Contains(r.UserId)));
        }

        return filtered.ToList();
    }

    public async Task<List<AttendeeExportRow>> GetAttendeeExportDataAsync()
    {
        var rows = await ticketRepository.GetAttendeeExportDataAsync();
        return rows.ToList();
    }

    public async Task<List<OrderExportRow>> GetOrderExportDataAsync()
    {
        var rows = await ticketRepository.GetOrderExportDataAsync();
        return rows.ToList();
    }

    public async Task<UserTicketHoldings> GetUserTicketHoldingsAsync(
        Guid userId, CancellationToken ct = default)
    {
        var orders = await ticketRepository.GetOrdersMatchedToUserAsync(userId, ct);
        var orderCount = orders.Count;
        var orderSummaries = orders.Select(o => new UserTicketOrderSummary(
            o.BuyerName,
            o.PurchasedAt,
            o.Attendees.Count,
            o.TotalAmount,
            o.Currency)).ToList();
        var openTicketOrderIds = orders
            .Where(o => o.PaymentStatus is TicketPaymentStatus.Paid or TicketPaymentStatus.Pending)
            .Select(o => o.Id)
            .ToList();

        var attendees = await ticketRepository.GetAttendeesVisibleToUserAsync(userId, ct);
        var tickets = attendees
            .Where(a => TicketAttendeeOwnership.IsCurrentOwner(a, userId))
            .OrderBy(a => a.Status == TicketAttendeeStatus.Void ? 1 : 0)
            .ThenBy(a => a.AttendeeName, StringComparer.OrdinalIgnoreCase)
            .Select(a => new UserTicketHoldingRow(
                a.Id,
                a.AttendeeName,
                a.AttendeeEmail,
                a.VendorTicketId,
                a.TicketTypeName,
                a.Status))
            .ToList();

        var ticketCount = await ComputeUserTicketCountAsync(userId);
        var syncState = await ticketRepository.GetSyncStateAsync(ct);
        var hasCurrentEventTicket = syncState is not null
            && !string.IsNullOrEmpty(syncState.VendorEventId)
            && await ticketRepository.HasEventTicketAsync(userId, syncState.VendorEventId, ct);
        var postEventHoldDate = hasCurrentEventTicket
            ? await GetPostEventHoldDateAsync(ct)
            : null;

        return new UserTicketHoldings(
            orderCount,
            tickets,
            hasCurrentEventTicket,
            ticketCount,
            postEventHoldDate)
        {
            OrderSummaries = orderSummaries,
            OpenTicketOrderIds = openTicketOrderIds,
        };
    }

    private async Task<Instant?> GetPostEventHoldDateAsync(CancellationToken ct = default)
    {
        var activeEvent = await shiftManagementService.GetActiveAsync();
        if (activeEvent is null)
            return null;

        var tz = DateTimeZoneProviders.Tzdb.GetZoneOrNull(activeEvent.TimeZoneId)
                 ?? DateTimeZone.Utc;
        var postEventDate = activeEvent.GateOpeningDate
            .PlusDays(activeEvent.StrikeEndOffset + 1);
        var postEventInstant = postEventDate
            .AtStartOfDayInZone(tz)
            .ToInstant();

        var now = clock.GetCurrentInstant();
        return postEventInstant > now ? postEventInstant : null;
    }

    public async Task<UserTicketExportData> GetUserTicketExportDataAsync(
        Guid userId, CancellationToken ct = default)
    {
        var orders = await ticketRepository.GetOrdersMatchedToUserAsync(userId, ct);
        var orderRows = orders
            .Select(o => new UserTicketOrderExportRow(
                o.BuyerName,
                o.BuyerEmail,
                o.TotalAmount,
                o.Currency,
                o.PaymentStatus.ToString(),
                o.DiscountCode,
                o.PurchasedAt))
            .ToList();

        var attendees = await ticketRepository.GetAttendeesMatchedToUserAsync(userId, ct);
        var attendeeRows = attendees
            .Select(a => new UserTicketAttendeeExportRow(
                a.AttendeeName,
                a.AttendeeEmail,
                a.TicketTypeName,
                a.Price,
                a.Status.ToString()))
            .ToList();

        return new UserTicketExportData(orderRows, attendeeRows);
    }

    private static TicketOrderInfo Project(TicketOrder o, string? currentEventId) => new(
        Id: o.Id,
        VendorOrderId: o.VendorOrderId,
        BuyerName: o.BuyerName,
        BuyerEmail: o.BuyerEmail,
        TotalAmount: o.TotalAmount,
        Currency: o.Currency,
        DiscountCode: o.DiscountCode,
        PaymentStatus: o.PaymentStatus,
        VendorEventId: o.VendorEventId,
        PurchasedAt: o.PurchasedAt,
        MatchedUserId: o.MatchedUserId,
        IsCurrentEvent: !string.IsNullOrEmpty(currentEventId)
            && string.Equals(o.VendorEventId, currentEventId, StringComparison.Ordinal),
        Attendees: o.Attendees.Select(a => new TicketAttendeeInfo(
            Id: a.Id,
            VendorTicketId: a.VendorTicketId,
            AttendeeName: a.AttendeeName,
            AttendeeEmail: a.AttendeeEmail,
            TicketTypeName: a.TicketTypeName,
            Price: a.Price,
            Status: a.Status,
            MatchedUserId: a.MatchedUserId)).ToList());

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(
        Guid userId, CancellationToken ct)
    {
        var export = await GetUserTicketExportDataAsync(userId, ct);

        var ordersSlice = new UserDataSlice(GdprExportSections.TicketOrders, export.Orders.Select(o => new
        {
            o.BuyerName,
            o.BuyerEmail,
            o.TotalAmount,
            o.Currency,
            o.PaymentStatus,
            o.DiscountCode,
            PurchasedAt = o.PurchasedAt.ToInvariantInstantString(),
        }).ToList());

        var attendeesSlice = new UserDataSlice(GdprExportSections.TicketAttendeeMatches, export.Attendees.Select(a => new
        {
            a.AttendeeName,
            a.AttendeeEmail,
            a.TicketTypeName,
            a.Price,
            a.Status,
        }).ToList());

        return [ordersSlice, attendeesSlice];
    }

    private static bool HasSearchTerm(
        [NotNullWhen(true)] string? value, int minLength = 2) =>
        !string.IsNullOrWhiteSpace(value) && value.Trim().Length >= minLength;

    private static bool ContainsIgnoreCase(string? source, string value) =>
        source?.Contains(value, StringComparison.OrdinalIgnoreCase) == true;

    // Invalidation no-ops on the inner; CachingTicketQueryService intercepts.
    public Task<IReadOnlyList<OrderDriftRow>> GetOrderDriftAsync(CancellationToken ct = default) =>
        ticketRepository.GetOrderDriftAsync(ct);

}
