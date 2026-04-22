using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Caching.Memory;
using NodaTime;
using Humans.Application.DTOs;
using Humans.Application.Extensions;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Constants;
using Humans.Domain.Enums;

namespace Humans.Application.Services.Tickets;

/// <summary>
/// Application-layer query service for the Tickets section. Owns
/// <c>ticket_orders</c> and <c>ticket_attendees</c> read access via
/// <see cref="ITicketRepository"/> and stitches cross-section data
/// (users, profiles, teams, emails, participation, campaigns, budget, event
/// settings) via their owning service interfaces.
/// </summary>
/// <remarks>
/// Migrated to <see cref="Humans.Application"/> per §15 (issue
/// nobodies-collective/Humans#545 sub-task #545a). Never imports EF types —
/// all DB access flows through <see cref="ITicketRepository"/>.
/// </remarks>
public sealed class TicketQueryService : ITicketQueryService, IUserDataContributor
{
    private static readonly TimeSpan TicketCountCacheTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan UserIdsWithTicketsCacheTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ValidAttendeeEmailsCacheTtl = TimeSpan.FromMinutes(5);

    private readonly ITicketRepository _ticketRepository;
    private readonly IMemoryCache _cache;
    private readonly IBudgetService _budgetService;
    private readonly ICampaignService _campaignService;
    private readonly IUserService _userService;
    private readonly IUserEmailService _userEmailService;
    private readonly IProfileService _profileService;
    private readonly ITeamService _teamService;
    private readonly IShiftManagementService _shiftManagementService;
    private readonly IClock _clock;

    public TicketQueryService(
        ITicketRepository ticketRepository,
        IMemoryCache cache,
        IBudgetService budgetService,
        ICampaignService campaignService,
        IUserService userService,
        IUserEmailService userEmailService,
        IProfileService profileService,
        ITeamService teamService,
        IShiftManagementService shiftManagementService,
        IClock clock)
    {
        _ticketRepository = ticketRepository;
        _cache = cache;
        _budgetService = budgetService;
        _campaignService = campaignService;
        _userService = userService;
        _userEmailService = userEmailService;
        _profileService = profileService;
        _teamService = teamService;
        _shiftManagementService = shiftManagementService;
        _clock = clock;
    }

    // ==========================================================================
    // Per-user ticket counts and match sets
    // ==========================================================================

    public async Task<int> GetUserTicketCountAsync(Guid userId)
    {
        var cacheKey = CacheKeys.UserTicketCount(userId);
        if (_cache.TryGetExistingValue(cacheKey, out int cached))
            return cached;

        var count = await GetUserTicketCountCoreAsync(userId);
        _cache.Set(cacheKey, count, TicketCountCacheTtl);
        return count;
    }

    private async Task<int> GetUserTicketCountCoreAsync(Guid userId)
    {
        // Match on attendees only — a buyer who purchased tickets for others
        // should NOT count as having a ticket themselves.

        // First check by MatchedUserId (set during sync).
        var matchedCount = await _ticketRepository
            .CountValidAttendeesMatchedToUserAsync(userId);
        if (matchedCount > 0)
            return matchedCount;

        // Fallback: check all verified user emails against attendee emails
        // case-insensitively. The attendee email set is cached and compared
        // in memory — avoids SQL upper() vs .NET ToUpperInvariant drift for
        // non-ASCII code points by applying a single StringComparer rule.
        var verifiedEmails = await _userEmailService.GetVerifiedEmailsForUserAsync(userId);
        if (verifiedEmails.Count == 0)
            return 0;

        var attendeeEmails = await GetValidAttendeeEmailsCachedAsync();
        if (attendeeEmails.Count == 0)
            return 0;

        var verifiedSet = verifiedEmails.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return attendeeEmails.Count(e => verifiedSet.Contains(e));
    }

    private Task<IReadOnlyList<string>> GetValidAttendeeEmailsCachedAsync() =>
        _cache.GetOrCreateAsync<IReadOnlyList<string>>(CacheKeys.ValidAttendeeEmails, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = ValidAttendeeEmailsCacheTtl;
            return await _ticketRepository.GetValidAttendeeEmailsAsync();
        })!;

    public async Task<HashSet<Guid>> GetUserIdsWithTicketsAsync()
    {
        return await _cache.GetOrCreateAsync(CacheKeys.UserIdsWithTickets, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = UserIdsWithTicketsCacheTtl;

            var ids = await _ticketRepository.GetValidMatchedAttendeeUserIdsAsync();
            return ids.ToHashSet();
        }) ?? [];
    }

    public async Task<List<string>> GetAvailableTicketTypesAsync()
    {
        var types = await _ticketRepository.GetDistinctTicketTypesAsync();
        return types.ToList();
    }

    public async Task<HashSet<Guid>> GetAllMatchedUserIdsAsync()
    {
        var fromAttendees = await _ticketRepository.GetAllMatchedAttendeeUserIdsAsync();
        var fromOrders = await _ticketRepository.GetAllMatchedOrderUserIdsAsync();
        return fromAttendees.Concat(fromOrders).ToHashSet();
    }

    // ==========================================================================
    // Dashboard stats
    // ==========================================================================

    public async Task<TicketDashboardStats> GetDashboardStatsAsync()
    {
        // Reset stuck Running state — if Running for > 30 min, treat as stale (crash recovery).
        var now = _clock.GetCurrentInstant();
        var staleThreshold = now - Duration.FromMinutes(30);
        var syncState = await _ticketRepository.ResetStaleRunningStateAsync(
            olderThan: staleThreshold,
            now: now,
            errorMessage: "Sync state was stuck in Running for >30 minutes (likely crash). Auto-reset.");

        var totals = await _ticketRepository.GetDashboardTotalsAsync();

        var revenue = totals.GrossRevenue;
        var totalStripeFees = totals.TotalStripeFees;
        var totalAppFees = totals.TotalApplicationFees;
        var ticketsSold = totals.TicketsSold;
        var netRevenue = revenue - totalStripeFees - totalAppFees;
        var avgPrice = ticketsSold > 0 ? netRevenue / ticketsSold : 0;
        var grossAvgPrice = ticketsSold > 0 ? revenue / ticketsSold : 0;
        var unmatchedCount = totals.UnmatchedOrderCount;

        // Fee breakdown by payment method (load in memory — small dataset).
        var feeData = await _ticketRepository.GetPaidOrderPaymentMethodsAsync();

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

        // Daily sales data for chart.
        var orderDates = await _ticketRepository.GetOrderDateAttendeeCountsAsync();

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

        // Recent 10 orders — MatchedUserName is stitched via IUserService.
        var recentOrders = await _ticketRepository.GetRecentOrdersAsync(count: 10);

        // Volunteer ticket coverage.
        var volunteerUserIds = await _teamService.GetActiveMemberUserIdsAsync(SystemTeamIds.Volunteers);
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

    // ==========================================================================
    // Cash-flow / break-even
    // ==========================================================================

    public Task<decimal> GetGrossTicketRevenueAsync() =>
        _ticketRepository.GetGrossPaidRevenueAsync();

    public async Task<BreakEvenResult> CalculateBreakEvenAsync(
        int ticketsSold,
        decimal grossRevenue,
        string currency,
        bool canAccessFinance,
        int fallbackTarget)
    {
        if (ticketsSold <= 0 || grossRevenue <= 0)
            return new BreakEvenResult { Target = fallbackTarget, Currency = currency };

        var activeBudgetYear = await _budgetService.GetActiveYearAsync();
        if (activeBudgetYear is null)
            return new BreakEvenResult { Target = fallbackTarget, Currency = currency };

        var summary = _budgetService.ComputeBudgetSummary(activeBudgetYear.Groups);
        var plannedExpenses = Math.Abs(summary.TotalExpenses);
        if (plannedExpenses <= 0)
            return new BreakEvenResult { Target = fallbackTarget, Currency = currency };

        // A = tickets sold so far, B = gross revenue so far, C = gross planned expenses
        // D = remaining expenses = C - B
        // E = gross average ticket price = B / A
        // F = remaining tickets = D / E
        // G = break-even target = A + F
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

        var breakEvenTarget = (long)ticketsSold + remainingTicketsToSell;
        var target = breakEvenTarget > int.MaxValue ? int.MaxValue : (int)breakEvenTarget;

        var detail = canAccessFinance
            ? $"{currency} {remainingExpenses:N2} remaining expenses / {currency} {grossAverageTicketPrice:N2} gross avg. per ticket = {remainingTicketsToSell:N0} tickets still to sell"
            : null;

        return new BreakEvenResult { Target = target, Detail = detail, Currency = currency };
    }

    // ==========================================================================
    // Code tracking (campaigns ↔ ticket orders)
    // ==========================================================================

    public async Task<CodeTrackingData> GetCodeTrackingDataAsync(string? search)
    {
        var campaignData = await _campaignService.GetCodeTrackingAsync();

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

        // Correlate redemption codes against ticket orders.
        var ordersWithCodes = await _ticketRepository.GetOrdersWithDiscountCodesAsync();
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

    // ==========================================================================
    // Sales aggregates
    // ==========================================================================

    public async Task<TicketSalesAggregates> GetSalesAggregatesAsync()
    {
        var orders = await _ticketRepository.GetPaidOrderSalesRowsAsync();

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

    // ==========================================================================
    // Paged admin list views
    // ==========================================================================

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

        var (rawRows, totalCount) = await _ticketRepository.GetOrdersPageAsync(
            search, sortBy, sortDesc, page, pageSize,
            parsedStatus, filterTicketType, filterMatched);

        var rows = await HydrateOrderMatchedUserNamesAsync(rawRows);
        return new OrdersPageResult { Rows = rows, TotalCount = totalCount };
    }

    public async Task<AttendeesPageResult> GetAttendeesPageAsync(
        string? search, string sortBy, bool sortDesc,
        int page, int pageSize,
        string? filterTicketType, string? filterStatus, bool? filterMatched, string? filterOrderId)
    {
        TicketAttendeeStatus? parsedStatus = null;
        if (!string.IsNullOrEmpty(filterStatus) &&
            Enum.TryParse<TicketAttendeeStatus>(filterStatus, true, out var status))
        {
            parsedStatus = status;
        }

        var (rawRows, totalCount) = await _ticketRepository.GetAttendeesPageAsync(
            search, sortBy, sortDesc, page, pageSize,
            filterTicketType, parsedStatus, filterMatched, filterOrderId);

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

        var users = await _userService.GetByIdsAsync(matchedIds);
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
                    MatchedUserName = user.DisplayName,
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

        var users = await _userService.GetByIdsAsync(matchedIds);
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
                    MatchedUserName = user.DisplayName,
                    VendorOrderId = r.VendorOrderId,
                };
            }
            return r;
        }).ToList();
    }

    // ==========================================================================
    // "Who hasn't bought" admin view — User × Profile × Teams × UserEmails
    // ==========================================================================

    public async Task<WhoHasntBoughtResult> GetWhoHasntBoughtAsync(
        string? search, string? filterTeam, string? filterTier, string? filterTicketStatus,
        int page, int pageSize)
    {
        var matchedUserIds = await GetAllMatchedUserIdsAsync();

        // Load Users and Volunteers-team membership via service interfaces.
        var allUsers = await _userService.GetAllUsersAsync();
        var volunteerUserIds = (await _teamService.GetActiveMemberUserIdsAsync(SystemTeamIds.Volunteers))
            .ToHashSet();

        var candidateIds = allUsers
            .Where(u => volunteerUserIds.Contains(u.Id))
            .Select(u => u.Id)
            .ToList();

        // Exclude humans who declared not attending this year.
        var activeEvent = await _shiftManagementService.GetActiveAsync();
        HashSet<Guid> notAttendingSet = [];
        if (activeEvent is not null && activeEvent.Year > 0)
        {
            var participations = await _userService.GetAllParticipationsForYearAsync(activeEvent.Year);
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

        // Stitch in memory: profiles (for tier), notification emails, team memberships.
        var profiles = await _profileService.GetByUserIdsAsync(candidateIds);
        var emailsById = await _userEmailService.GetNotificationEmailsByUserIdsAsync(candidateIds);
        var teamsByUser = await _teamService.GetActiveNonSystemTeamNamesByUserIdsAsync(candidateIds);

        var usersById = allUsers.ToDictionary(u => u.Id);

        var rows = candidateIds
            .Where(id => profiles.ContainsKey(id))
            .Select(id =>
            {
                var user = usersById[id];
                var profile = profiles[id];
                emailsById.TryGetValue(id, out var email);
                teamsByUser.TryGetValue(id, out var teamNames);

                return new WhoHasntBoughtRow
                {
                    UserId = id,
                    HasTicket = matchedUserIds.Contains(id),
                    Name = user.DisplayName,
                    Email = email ?? string.Empty,
                    TeamNames = teamNames ?? [],
                    Tier = profile.MembershipTier,
                };
            })
            .ToList();

        // Email search against the full UserEmail set (not just the notification
        // target) so admins can find humans whose ticket-vendor address differs
        // from their primary notification email.
        HashSet<Guid>? emailMatchUserIds = null;
        if (HasSearchTerm(search, 1))
        {
            var matches = await _userEmailService.SearchUserIdsByVerifiedEmailAsync(search!);
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
            // Match against display name, notification email, and any verified
            // email belonging to the user (via emailMatchUserIds). Admins rely
            // on the verified-email match when a ticket was purchased under a
            // secondary address.
            filtered = filtered.Where(r =>
                ContainsIgnoreCase(r.Name, search) ||
                ContainsIgnoreCase(r.Email, search) ||
                (emailMatchUserIds is not null && emailMatchUserIds.Contains(r.UserId)));
        }

        return filtered.ToList();
    }

    // ==========================================================================
    // Exports
    // ==========================================================================

    public async Task<List<AttendeeExportRow>> GetAttendeeExportDataAsync()
    {
        var rows = await _ticketRepository.GetAttendeeExportDataAsync();
        return rows.ToList();
    }

    public async Task<List<OrderExportRow>> GetOrderExportDataAsync()
    {
        var rows = await _ticketRepository.GetOrderExportDataAsync();
        return rows.ToList();
    }

    // ==========================================================================
    // Per-user match probes (used by ProfileService / GuestController)
    // ==========================================================================

    public Task<bool> HasTicketAttendeeMatchAsync(Guid userId) =>
        _ticketRepository.HasAnyTicketMatchAsync(userId);

    public async Task<List<UserTicketOrderSummary>> GetUserTicketOrderSummariesAsync(Guid userId)
    {
        var orders = await _ticketRepository.GetOrdersMatchedToUserAsync(userId);
        return orders.Select(o => new UserTicketOrderSummary(
            o.BuyerName,
            o.PurchasedAt,
            o.Attendees.Count,
            o.TotalAmount,
            o.Currency)).ToList();
    }

    public async Task<Instant?> GetPostEventHoldDateAsync(CancellationToken ct = default)
    {
        var activeEvent = await _shiftManagementService.GetActiveAsync();
        if (activeEvent is null)
            return null;

        var tz = DateTimeZoneProviders.Tzdb.GetZoneOrNull(activeEvent.TimeZoneId)
                 ?? DateTimeZone.Utc;
        var postEventDate = activeEvent.GateOpeningDate
            .PlusDays(activeEvent.StrikeEndOffset + 1);
        var postEventInstant = postEventDate
            .AtStartOfDayInZone(tz)
            .ToInstant();

        var now = _clock.GetCurrentInstant();
        return postEventInstant > now ? postEventInstant : null;
    }

    public async Task<bool> HasCurrentEventTicketAsync(Guid userId, CancellationToken ct = default)
    {
        var syncState = await _ticketRepository.GetSyncStateAsync(ct);
        if (syncState is null || string.IsNullOrEmpty(syncState.VendorEventId))
            return false;

        return await _ticketRepository.HasEventTicketAsync(userId, syncState.VendorEventId, ct);
    }

    public async Task<UserTicketExportData> GetUserTicketExportDataAsync(
        Guid userId, CancellationToken ct = default)
    {
        var orders = await _ticketRepository.GetOrdersMatchedToUserAsync(userId, ct);
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

        var attendees = await _ticketRepository.GetAttendeesMatchedToUserAsync(userId, ct);
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

    public async Task<IReadOnlyCollection<Guid>> GetMatchedUserIdsForPaidOrdersAsync(
        CancellationToken ct = default)
    {
        var ids = await _ticketRepository.GetMatchedUserIdsForPaidOrdersAsync(ct);
        return ids;
    }

    public Task<IReadOnlyList<Instant>> GetPaidOrderDatesInWindowAsync(
        Instant fromInclusive,
        Instant toExclusive,
        CancellationToken ct = default) =>
        _ticketRepository.GetPaidOrderDatesInWindowAsync(fromInclusive, toExclusive, ct);

    // ==========================================================================
    // GDPR data export contributor
    // ==========================================================================

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

    // ==========================================================================
    // Helpers
    // ==========================================================================

    private static bool HasSearchTerm(
        [NotNullWhen(true)] string? value, int minLength = 2) =>
        !string.IsNullOrWhiteSpace(value) && value.Trim().Length >= minLength;

    private static bool ContainsIgnoreCase(string? source, string value) =>
        source?.Contains(value, StringComparison.OrdinalIgnoreCase) == true;
}
