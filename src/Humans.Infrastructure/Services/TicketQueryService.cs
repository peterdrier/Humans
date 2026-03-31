using Microsoft.EntityFrameworkCore;
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
    private readonly HumansDbContext _dbContext;

    public TicketQueryService(HumansDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<int> GetUserTicketCountAsync(Guid userId)
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
        // Match on attendees only — a buyer who purchased tickets for others
        // should NOT count as having a ticket themselves.
        var attendeeUserIds = await _dbContext.TicketAttendees
            .Where(a => a.MatchedUserId != null &&
                (a.Status == TicketAttendeeStatus.Valid || a.Status == TicketAttendeeStatus.CheckedIn))
            .Select(a => a.MatchedUserId!.Value)
            .Distinct()
            .ToListAsync();

        return attendeeUserIds.ToHashSet();
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
        var totalStripeFees = await paidOrders.SumAsync(o => (decimal?)o.StripeFee ?? 0m);
        var totalAppFees = await paidOrders.SumAsync(o => (decimal?)o.ApplicationFee ?? 0m);
        var netRevenue = revenue - totalStripeFees - totalAppFees;
        var avgPrice = ticketsSold > 0 ? netRevenue / ticketsSold : 0;
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
}
