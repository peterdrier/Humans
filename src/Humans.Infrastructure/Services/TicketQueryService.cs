using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Humans.Application;
using Humans.Application.DTOs;
using Humans.Application.Extensions;
using Humans.Application.Interfaces;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using NodaTime;

namespace Humans.Infrastructure.Services;

public class TicketQueryService : ITicketQueryService
{
    private static readonly TimeSpan TicketCountCacheTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DashboardStatsCacheTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan UserIdsWithTicketsCacheTtl = TimeSpan.FromMinutes(5);

    private readonly HumansDbContext _dbContext;
    private readonly IMemoryCache _cache;
    private readonly IBudgetService _budgetService;

    public TicketQueryService(HumansDbContext dbContext, IMemoryCache cache, IBudgetService budgetService)
    {
        _dbContext = dbContext;
        _cache = cache;
        _budgetService = budgetService;
    }

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

        // First check by MatchedUserId (set during sync)
        var attendeeCount = await _dbContext.TicketAttendees.CountAsync(a =>
            a.MatchedUserId == userId &&
            (a.Status == TicketAttendeeStatus.Valid || a.Status == TicketAttendeeStatus.CheckedIn));

        if (attendeeCount > 0)
            return attendeeCount;

        // Fallback: check all verified user emails against attendee emails (case-insensitive)
        // ToUpper() translates to SQL UPPER() in EF/Npgsql — analyzer MA0011 is a false positive here
#pragma warning disable MA0011
        var userEmails = await _dbContext.Set<UserEmail>()
            .Where(e => e.UserId == userId && e.IsVerified)
            .Select(e => e.Email.ToUpper())
            .ToListAsync();

        if (userEmails.Count == 0)
            return 0;

        attendeeCount = await _dbContext.TicketAttendees.CountAsync(a =>
            a.AttendeeEmail != null &&
            userEmails.Contains(a.AttendeeEmail.ToUpper()) &&
            (a.Status == TicketAttendeeStatus.Valid || a.Status == TicketAttendeeStatus.CheckedIn));
#pragma warning restore MA0011

        return attendeeCount;
    }

    public async Task<HashSet<Guid>> GetUserIdsWithTicketsAsync()
    {
        return await _cache.GetOrCreateAsync(CacheKeys.UserIdsWithTickets, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = UserIdsWithTicketsCacheTtl;

            // Match on attendees only — a buyer who purchased tickets for others
            // should NOT count as having a ticket themselves.
            var attendeeUserIds = await _dbContext.TicketAttendees
                .Where(a => a.MatchedUserId != null &&
                    (a.Status == TicketAttendeeStatus.Valid || a.Status == TicketAttendeeStatus.CheckedIn))
                .Select(a => a.MatchedUserId!.Value)
                .Distinct()
                .ToListAsync();

            return attendeeUserIds.ToHashSet();
        }) ?? [];
    }

    public Task<List<string>> GetAvailableTicketTypesAsync()
    {
        return _dbContext.TicketAttendees
            .Select(a => a.TicketTypeName)
            .Distinct()
            .OrderBy(t => t)
            .ToListAsync();
    }

    public async Task<HashSet<Guid>> GetAllMatchedUserIdsAsync()
    {
        var matchedFromAttendees = await _dbContext.TicketAttendees
            .Where(a => a.MatchedUserId != null)
            .Select(a => a.MatchedUserId!.Value)
            .Distinct()
            .ToListAsync();

        var matchedFromOrders = await _dbContext.TicketOrders
            .Where(o => o.MatchedUserId != null)
            .Select(o => o.MatchedUserId!.Value)
            .Distinct()
            .ToListAsync();

        return matchedFromAttendees.Union(matchedFromOrders).ToHashSet();
    }

    public async Task<TicketDashboardStats> GetDashboardStatsAsync()
    {
        // Reset stuck Running state — if Running for > 30 min, treat as stale (crash recovery)
        var syncState = await _dbContext.TicketSyncStates.FindAsync(1);
        if (syncState is { SyncStatus: TicketSyncStatus.Running, StatusChangedAt: not null })
        {
            var elapsed = SystemClock.Instance.GetCurrentInstant() - syncState.StatusChangedAt.Value;
            if (elapsed > Duration.FromMinutes(30))
            {
                syncState.SyncStatus = TicketSyncStatus.Error;
                syncState.LastError = "Sync state was stuck in Running for >30 minutes (likely crash). Auto-reset.";
                syncState.StatusChangedAt = SystemClock.Instance.GetCurrentInstant();
                await _dbContext.SaveChangesAsync();
            }
        }

        var orders = _dbContext.TicketOrders.AsQueryable();
        var attendees = _dbContext.TicketAttendees.AsQueryable();

        // Count both Valid and CheckedIn — both are sold tickets
        var ticketsSold = await attendees.CountAsync(a =>
            a.Status == TicketAttendeeStatus.Valid || a.Status == TicketAttendeeStatus.CheckedIn);
        var paidOrders = orders.Where(o => o.PaymentStatus == TicketPaymentStatus.Paid);
        var revenue = await paidOrders.SumAsync(o => o.TotalAmount);
        var totalStripeFees = await paidOrders.SumAsync(o => o.StripeFee ?? 0m);
        var totalAppFees = await paidOrders.SumAsync(o => o.ApplicationFee ?? 0m);
        var netRevenue = revenue - totalStripeFees - totalAppFees;
        var avgPrice = ticketsSold > 0 ? netRevenue / ticketsSold : 0;
        var grossAvgPrice = ticketsSold > 0 ? revenue / ticketsSold : 0;
        var unmatchedCount = await orders.CountAsync(o => o.MatchedUserId == null);

        // Fee breakdown by payment method (load in memory — small dataset)
        var feeData = await paidOrders
            .Where(o => o.PaymentMethod != null)
            .Select(o => new { o.PaymentMethod, o.PaymentMethodDetail, o.TotalAmount, o.StripeFee, o.ApplicationFee })
            .ToListAsync();

        var feesByMethod = feeData
            .GroupBy(o => o.PaymentMethodDetail != null ? $"{o.PaymentMethod}/{o.PaymentMethodDetail}" : o.PaymentMethod!, StringComparer.Ordinal)
            .Select(g =>
            {
                var totalAmt = g.Sum(o => o.TotalAmount);
                var totalStripe = g.Sum(o => o.StripeFee ?? 0);
                return new FeeBreakdownByMethod
                {
                    PaymentMethod = g.Key,
                    OrderCount = g.Count(),
                    TotalAmount = totalAmt,
                    TotalStripeFees = totalStripe,
                    TotalApplicationFees = g.Sum(o => o.ApplicationFee ?? 0),
                    EffectiveRate = totalAmt > 0 ? Math.Round(totalStripe / totalAmt * 100, 2) : 0
                };
            })
            .OrderByDescending(f => f.TotalStripeFees)
            .ToList();

        // Daily sales data for chart.
        // NodaTime GroupBy is not translatable by EF Core, so load in memory first.
        // At ~1,500 orders this is fine for a small nonprofit system.
        var orderDates = await orders
            .Select(o => new { o.PurchasedAt, AttendeeCount = o.Attendees.Count })
            .ToListAsync();

        var salesByDate = orderDates
            .GroupBy(o => o.PurchasedAt.InUtc().Date)
            .ToDictionary(g => g.Key, g => g.Sum(o => o.AttendeeCount));

        // Fill in zero-sale days so chart and rolling average are correct
        var dailySalesPoints = new List<DailySales>();
        if (salesByDate.Count > 0)
        {
            var startDate = salesByDate.Keys.Min();
            var endDate = salesByDate.Keys.Max();
            var allDays = new List<(LocalDate Date, int Count)>();

            for (var d = startDate; d <= endDate; d = d.PlusDays(1))
            {
                allDays.Add((d, salesByDate.GetValueOrDefault(d, 0)));
            }

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
                    RollingAverage = Math.Round(rollingAvg, 1)
                });
            }
        }

        // Recent 10 orders
        var recentOrders = await orders
            .OrderByDescending(o => o.PurchasedAt)
            .Take(10)
            .Select(o => new RecentOrder
            {
                Id = o.Id,
                BuyerName = o.BuyerName,
                TicketCount = o.Attendees.Count,
                Amount = o.TotalAmount,
                Currency = o.Currency,
                PurchasedAt = o.PurchasedAt,
                IsMatched = o.MatchedUserId != null
            })
            .ToListAsync();

        // Volunteer ticket coverage
        var volunteersTeamId = SystemTeamIds.Volunteers;

        var totalActiveVolunteers = await _dbContext.Set<TeamMember>()
            .CountAsync(tm => tm.TeamId == volunteersTeamId && tm.LeftAt == null);

        var userIdsWithTickets = await GetUserIdsWithTicketsAsync();

        var volunteersWithTickets = await _dbContext.Set<TeamMember>()
            .Where(tm => tm.TeamId == volunteersTeamId && tm.LeftAt == null)
            .CountAsync(tm => userIdsWithTickets.Contains(tm.UserId));

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
            RecentOrders = recentOrders,
            SyncStatus = syncState?.SyncStatus ?? TicketSyncStatus.Idle,
            SyncError = syncState?.LastError,
            LastSyncAt = syncState?.LastSyncAt,
            TotalActiveVolunteers = totalActiveVolunteers,
            VolunteersWithTickets = volunteersWithTickets,
            VolunteerCoveragePercent = volunteerCoveragePct,
        };
    }

    public async Task<decimal> GetGrossTicketRevenueAsync()
    {
        return await _dbContext.TicketOrders
            .Where(o => o.PaymentStatus == TicketPaymentStatus.Paid)
            .SumAsync(o => o.TotalAmount);
    }

    public async Task<BreakEvenResult> CalculateBreakEvenAsync(int ticketsSold, decimal grossRevenue, string currency, bool canAccessFinance, int fallbackTarget)
    {
        if (ticketsSold <= 0 || grossRevenue <= 0)
        {
            return new BreakEvenResult { Target = fallbackTarget, Currency = currency };
        }

        var activeBudgetYear = await _budgetService.GetActiveYearAsync();
        if (activeBudgetYear is null)
        {
            return new BreakEvenResult { Target = fallbackTarget, Currency = currency };
        }

        // Use the same summary computation as the Finance page so break-even reflects
        // total planned expenses including VAT projections. The canAccessFinance flag only
        // controls whether the detail breakdown string is shown.
        var summary = _budgetService.ComputeBudgetSummary(activeBudgetYear.Groups);
        var plannedExpenses = Math.Abs(summary.TotalExpenses);
        if (plannedExpenses <= 0)
        {
            return new BreakEvenResult { Target = fallbackTarget, Currency = currency };
        }

        // Break-even target from current realized gross average revenue per ticket:
        // A = tickets sold so far, B = gross revenue so far, C = gross planned expenses
        // D = remaining expenses = C - B
        // E = gross average ticket price = B / A
        // F = remaining tickets = D / E
        // G = break-even target = A + F
        // Finance hover shows D / E = F tickets still to sell.
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
        var target = breakEvenTarget > int.MaxValue
            ? int.MaxValue
            : (int)breakEvenTarget;

        var detail = canAccessFinance
            ? $"{currency} {remainingExpenses:N2} remaining expenses / {currency} {grossAverageTicketPrice:N2} gross avg. per ticket = {remainingTicketsToSell:N0} tickets still to sell"
            : null;

        return new BreakEvenResult { Target = target, Detail = detail, Currency = currency };
    }

    public async Task<CodeTrackingData> GetCodeTrackingDataAsync(string? search)
    {
        var campaigns = await _dbContext.Set<Campaign>()
            .Where(c => c.Status == CampaignStatus.Active || c.Status == CampaignStatus.Completed)
            .Include(c => c.Grants).ThenInclude(g => g.Code)
            .Include(c => c.Grants).ThenInclude(g => g.User)
            .OrderByDescending(c => c.CreatedAt)
            .AsSplitQuery()
            .ToListAsync();

        var campaignSummaries = campaigns.Select(c =>
        {
            var total = c.Grants.Count;
            var redeemed = c.Grants.Count(g => g.RedeemedAt is not null);
            return new CampaignCodeSummaryDto
            {
                CampaignId = c.Id,
                CampaignTitle = c.Title,
                TotalGrants = total,
                Redeemed = redeemed,
                Unused = total - redeemed,
                RedemptionRate = total > 0 ? Math.Round(redeemed * 100m / total, 1) : 0
            };
        }).ToList();

        var allGrants = campaigns.SelectMany(c => c.Grants).AsEnumerable();
        if (!string.IsNullOrWhiteSpace(search) && search.Trim().Length >= 1)
        {
            allGrants = allGrants.Where(g =>
                g.Code?.Code.Contains(search, StringComparison.OrdinalIgnoreCase) == true ||
                g.User.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        // Load orders with discount codes to correlate redemptions
        var ordersWithCodes = await _dbContext.TicketOrders
            .Where(o => o.DiscountCode != null)
            .Select(o => new { o.DiscountCode, o.BuyerName, o.BuyerEmail, o.VendorOrderId })
            .ToListAsync();

        var orderByCode = ordersWithCodes
            .GroupBy(o => o.DiscountCode!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var codeRows = allGrants.Select(g =>
        {
            var code = g.Code?.Code;
            orderByCode.TryGetValue(code ?? "", out var matchedOrder);
            return new CodeDetailDto
            {
                Code = code ?? "\u2014",
                RecipientName = g.User.DisplayName,
                RecipientUserId = g.UserId,
                CampaignTitle = campaigns.First(c => c.Id == g.CampaignId).Title,
                Status = g.RedeemedAt is not null ? "Redeemed" : (g.LatestEmailStatus?.ToString() ?? "Pending"),
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
        // Load all paid orders with attendee data in memory (small dataset at ~1,500 orders)
        var orders = await _dbContext.TicketOrders
            .Where(o => o.PaymentStatus == TicketPaymentStatus.Paid)
            .Select(o => new
            {
                o.PurchasedAt,
                o.TotalAmount,
                o.DonationAmount,
                o.VatAmount,
                AttendeeCount = o.Attendees.Count(a =>
                    a.Status == TicketAttendeeStatus.Valid || a.Status == TicketAttendeeStatus.CheckedIn),
                // VIP donations: sum of (price - threshold) for attendees priced above the threshold
                VipDonations = o.Attendees
                    .Where(a =>
                        (a.Status == TicketAttendeeStatus.Valid || a.Status == TicketAttendeeStatus.CheckedIn) &&
                        a.Price > TicketConstants.VipThresholdEuros)
                    .Sum(a => a.Price - TicketConstants.VipThresholdEuros)
            })
            .ToListAsync();

        // Weekly aggregates (ISO weeks, Mon–Sun)
        var weeklySales = orders
            .GroupBy(o =>
            {
                var date = o.PurchasedAt.InUtc().Date;
                // NodaTime ISO day of week: Monday=1, Sunday=7
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

        // Quarterly aggregates (Spanish tax quarters: Q1=Jan-Mar, Q2=Apr-Jun, Q3=Jul-Sep, Q4=Oct-Dec)
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
        var query = BuildOrdersQuery(search, filterPaymentStatus, filterTicketType, filterMatched);

        var totalCount = await query.CountAsync();

        query = ApplyOrderSorting(query, sortBy, sortDesc);

        var rows = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(o => new OrderRow
            {
                Id = o.Id,
                VendorOrderId = o.VendorOrderId,
                PurchasedAt = o.PurchasedAt,
                BuyerName = o.BuyerName,
                BuyerEmail = o.BuyerEmail,
                AttendeeCount = o.Attendees.Count,
                TotalAmount = o.TotalAmount,
                Currency = o.Currency,
                DiscountCode = o.DiscountCode,
                DiscountAmount = o.DiscountAmount,
                DonationAmount = o.DonationAmount,
                VatAmount = o.VatAmount,
                PaymentMethod = o.PaymentMethod,
                PaymentMethodDetail = o.PaymentMethodDetail,
                StripeFee = o.StripeFee,
                ApplicationFee = o.ApplicationFee,
                PaymentStatus = o.PaymentStatus,
                VendorDashboardUrl = o.VendorDashboardUrl,
                MatchedUserId = o.MatchedUserId,
                MatchedUserName = o.MatchedUser != null ? o.MatchedUser.DisplayName : null
            })
            .ToListAsync();

        return new OrdersPageResult { Rows = rows, TotalCount = totalCount };
    }

    public async Task<AttendeesPageResult> GetAttendeesPageAsync(
        string? search, string sortBy, bool sortDesc,
        int page, int pageSize,
        string? filterTicketType, string? filterStatus, bool? filterMatched, string? filterOrderId)
    {
        var query = BuildAttendeesQuery(search, filterTicketType, filterStatus, filterMatched, filterOrderId);

        var totalCount = await query.CountAsync();

        query = ApplyAttendeeSorting(query, sortBy, sortDesc);

        var rows = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(a => new AttendeeRow
            {
                Id = a.Id,
                AttendeeName = a.AttendeeName,
                AttendeeEmail = a.AttendeeEmail,
                TicketTypeName = a.TicketTypeName,
                Price = a.Price,
                Status = a.Status,
                MatchedUserId = a.MatchedUserId,
                MatchedUserName = a.MatchedUser != null ? a.MatchedUser.DisplayName : null,
                VendorOrderId = a.TicketOrder.VendorOrderId
            })
            .ToListAsync();

        return new AttendeesPageResult { Rows = rows, TotalCount = totalCount };
    }

    public async Task<WhoHasntBoughtResult> GetWhoHasntBoughtAsync(
        string? search, string? filterTeam, string? filterTier, string? filterTicketStatus,
        int page, int pageSize)
    {
        var matchedUserIds = await GetAllMatchedUserIdsAsync();

        // Load ALL active humans (not just unmatched) so we can toggle between views
        var users = await _dbContext.Users
            .Include(u => u.Profile)
            .Include(u => u.UserEmails)
            .Include(u => u.TeamMemberships).ThenInclude(tm => tm.Team)
            .AsSplitQuery()
            .ToListAsync();

        var volunteersTeamId = SystemTeamIds.Volunteers;

        var activeHumans = users
            .Where(u => u.Profile is not null &&
                u.TeamMemberships.Any(tm => tm.TeamId == volunteersTeamId && tm.LeftAt == null))
            .ToList();

        var filteredHumans = FilterWhoHasntBoughtHumans(
            activeHumans,
            matchedUserIds,
            filterTicketStatus,
            filterTeam,
            filterTier,
            search);

        var totalCount = filteredHumans.Count;
        var pagedHumans = filteredHumans
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(u => new WhoHasntBoughtRowDto
            {
                UserId = u.Id,
                HasTicket = matchedUserIds.Contains(u.Id),
                Name = u.DisplayName,
                Email = u.UserEmails.FirstOrDefault(e => e.IsNotificationTarget)?.Email ?? string.Empty,
                Teams = string.Join(", ", u.TeamMemberships
                    .Where(tm => tm.LeftAt == null)
                    .Select(tm => tm.Team.Name)
                    .Order(StringComparer.OrdinalIgnoreCase)),
                Tier = u.Profile?.MembershipTier ?? MembershipTier.Volunteer,
            })
            .ToList();

        var teams = activeHumans
            .SelectMany(u => u.TeamMemberships)
            .Where(tm => tm.LeftAt == null)
            .Select(tm => tm.Team.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new WhoHasntBoughtResult
        {
            Humans = pagedHumans,
            TotalCount = totalCount,
            AvailableTeams = teams,
        };
    }

    public async Task<List<AttendeeExportRow>> GetAttendeeExportDataAsync()
    {
        return await _dbContext.TicketAttendees
            .Include(a => a.TicketOrder)
            .OrderBy(a => a.AttendeeName)
            .Select(a => new AttendeeExportRow
            {
                AttendeeName = a.AttendeeName,
                AttendeeEmail = a.AttendeeEmail,
                TicketTypeName = a.TicketTypeName,
                Price = a.Price,
                Status = a.Status.ToString(),
                VendorOrderId = a.TicketOrder.VendorOrderId
            })
            .ToListAsync();
    }

    public async Task<List<OrderExportRow>> GetOrderExportDataAsync()
    {
        var orders = await _dbContext.TicketOrders
            .Include(o => o.Attendees)
            .OrderByDescending(o => o.PurchasedAt)
            .ToListAsync();

        return orders.Select(o =>
        {
            var method = o.PaymentMethodDetail != null
                ? $"{o.PaymentMethod}/{o.PaymentMethodDetail}"
                : o.PaymentMethod;
            return new OrderExportRow
            {
                Date = o.PurchasedAt.InUtc().Date.ToIsoDateString(),
                BuyerName = o.BuyerName,
                BuyerEmail = o.BuyerEmail,
                AttendeeCount = o.Attendees.Count,
                TotalAmount = o.TotalAmount,
                Currency = o.Currency,
                DiscountCode = o.DiscountCode,
                DiscountAmount = o.DiscountAmount,
                DonationAmount = o.DonationAmount,
                VatAmount = o.VatAmount,
                PaymentMethod = method,
                StripeFee = o.StripeFee,
                ApplicationFee = o.ApplicationFee,
                PaymentStatus = o.PaymentStatus.ToString()
            };
        }).ToList();
    }

    private IQueryable<TicketOrder> BuildOrdersQuery(
        string? search,
        string? filterPaymentStatus,
        string? filterTicketType,
        bool? filterMatched)
    {
        var query = _dbContext.TicketOrders
            .Include(o => o.Attendees)
            .Include(o => o.MatchedUser)
            .AsQueryable();

        if (HasSearchTerm(search, 1))
        {
            var normalizedSearch = search.ToLowerInvariant();
#pragma warning disable MA0011 // EF LINQ: ToLower() translates to SQL lower()
            query = query.Where(o =>
                o.BuyerName.ToLower().Contains(normalizedSearch) ||
                o.BuyerEmail.ToLower().Contains(normalizedSearch) ||
                o.VendorOrderId.ToLower().Contains(normalizedSearch) ||
                (o.DiscountCode != null && o.DiscountCode.ToLower().Contains(normalizedSearch)));
#pragma warning restore MA0011
        }

        if (!string.IsNullOrEmpty(filterPaymentStatus) &&
            Enum.TryParse<TicketPaymentStatus>(filterPaymentStatus, true, out var paymentStatus))
        {
            query = query.Where(o => o.PaymentStatus == paymentStatus);
        }

        if (!string.IsNullOrEmpty(filterTicketType))
        {
            query = query.Where(o => o.Attendees.Any(a => a.TicketTypeName == filterTicketType));
        }

        if (filterMatched == true)
        {
            query = query.Where(o => o.MatchedUserId != null);
        }
        else if (filterMatched == false)
        {
            query = query.Where(o => o.MatchedUserId == null);
        }

        return query;
    }

    private static IQueryable<TicketOrder> ApplyOrderSorting(
        IQueryable<TicketOrder> query,
        string? sortBy,
        bool sortDesc)
    {
        if (string.Equals(sortBy, "amount", StringComparison.OrdinalIgnoreCase))
        {
            return sortDesc ? query.OrderByDescending(o => o.TotalAmount) : query.OrderBy(o => o.TotalAmount);
        }

        if (string.Equals(sortBy, "name", StringComparison.OrdinalIgnoreCase))
        {
            return sortDesc ? query.OrderByDescending(o => o.BuyerName) : query.OrderBy(o => o.BuyerName);
        }

        if (string.Equals(sortBy, "tickets", StringComparison.OrdinalIgnoreCase))
        {
            return sortDesc ? query.OrderByDescending(o => o.Attendees.Count) : query.OrderBy(o => o.Attendees.Count);
        }

        return sortDesc ? query.OrderByDescending(o => o.PurchasedAt) : query.OrderBy(o => o.PurchasedAt);
    }

    private IQueryable<TicketAttendee> BuildAttendeesQuery(
        string? search,
        string? filterTicketType,
        string? filterStatus,
        bool? filterMatched,
        string? filterOrderId)
    {
        var query = _dbContext.TicketAttendees
            .Include(a => a.MatchedUser)
            .Include(a => a.TicketOrder)
            .AsQueryable();

        if (!string.IsNullOrEmpty(filterOrderId))
        {
            query = query.Where(a => a.TicketOrder.VendorOrderId == filterOrderId);
        }

        if (HasSearchTerm(search, 1))
        {
            var normalizedSearch = search.ToLowerInvariant();
#pragma warning disable MA0011 // EF LINQ: ToLower() translates to SQL lower()
            query = query.Where(a =>
                a.AttendeeName.ToLower().Contains(normalizedSearch) ||
                (a.AttendeeEmail != null && a.AttendeeEmail.ToLower().Contains(normalizedSearch)));
#pragma warning restore MA0011
        }

        if (!string.IsNullOrEmpty(filterTicketType))
        {
            query = query.Where(a => a.TicketTypeName == filterTicketType);
        }

        if (!string.IsNullOrEmpty(filterStatus) &&
            Enum.TryParse<TicketAttendeeStatus>(filterStatus, true, out var status))
        {
            query = query.Where(a => a.Status == status);
        }

        if (filterMatched == true)
        {
            query = query.Where(a => a.MatchedUserId != null);
        }
        else if (filterMatched == false)
        {
            query = query.Where(a => a.MatchedUserId == null);
        }

        return query;
    }

    private static IQueryable<TicketAttendee> ApplyAttendeeSorting(
        IQueryable<TicketAttendee> query,
        string? sortBy,
        bool sortDesc)
    {
        if (string.Equals(sortBy, "type", StringComparison.OrdinalIgnoreCase))
        {
            return sortDesc ? query.OrderByDescending(a => a.TicketTypeName) : query.OrderBy(a => a.TicketTypeName);
        }

        if (string.Equals(sortBy, "price", StringComparison.OrdinalIgnoreCase))
        {
            return sortDesc ? query.OrderByDescending(a => a.Price) : query.OrderBy(a => a.Price);
        }

        if (string.Equals(sortBy, "status", StringComparison.OrdinalIgnoreCase))
        {
            return sortDesc ? query.OrderByDescending(a => a.Status) : query.OrderBy(a => a.Status);
        }

        return sortDesc ? query.OrderByDescending(a => a.AttendeeName) : query.OrderBy(a => a.AttendeeName);
    }

    private static List<User> FilterWhoHasntBoughtHumans(
        IEnumerable<User> humans,
        HashSet<Guid> matchedUserIds,
        string? filterTicketStatus,
        string? filterTeam,
        string? filterTier,
        string? search)
    {
        var filteredHumans = humans;

        if (string.Equals(filterTicketStatus, "bought", StringComparison.OrdinalIgnoreCase))
        {
            filteredHumans = filteredHumans.Where(u => matchedUserIds.Contains(u.Id));
        }
        else if (string.Equals(filterTicketStatus, "not_bought", StringComparison.OrdinalIgnoreCase))
        {
            filteredHumans = filteredHumans.Where(u => !matchedUserIds.Contains(u.Id));
        }

        if (!string.IsNullOrEmpty(filterTeam))
        {
            filteredHumans = filteredHumans.Where(u =>
                u.TeamMemberships.Any(tm =>
                    tm.LeftAt == null &&
                    string.Equals(tm.Team.Name, filterTeam, StringComparison.OrdinalIgnoreCase)));
        }

        if (!string.IsNullOrEmpty(filterTier) && Enum.TryParse<MembershipTier>(filterTier, ignoreCase: true, out var parsedTier))
        {
            filteredHumans = filteredHumans.Where(u => u.Profile?.MembershipTier == parsedTier);
        }

        if (HasSearchTerm(search, 1))
        {
            filteredHumans = filteredHumans.Where(u =>
                ContainsIgnoreCase(u.DisplayName, search) ||
                u.UserEmails.Any(e => ContainsIgnoreCase(e.Email, search)));
        }

        return filteredHumans.ToList();
    }

    private static bool HasSearchTerm([NotNullWhen(true)] string? value, int minLength = 2) =>
        !string.IsNullOrWhiteSpace(value) && value.Trim().Length >= minLength;

    private static bool ContainsIgnoreCase(string? source, string value) =>
        source?.Contains(value, StringComparison.OrdinalIgnoreCase) == true;
}
