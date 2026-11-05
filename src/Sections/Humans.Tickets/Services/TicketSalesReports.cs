using Humans.Base.Extensions;
using Humans.Base.Interfaces;
using Humans.Budget.Contracts;
using Humans.Tickets.Domain;
using Humans.Campaigns.Contracts;
using Humans.Tickets.Data;
using Humans.Tickets.Services.Dtos;
using NodaTime;

namespace Humans.Tickets.Services;

internal sealed class TicketSalesReports(
    ITicketRepository ticketRepository,
    ICampaignService campaignService,
    IBudgetService budgetService,
    IClock clock) : IApplicationService
{
    public async Task<TicketDashboardStats> GetDashboardStatsAsync()
    {
        // Crash recovery: a sync stuck in Running for 30 minutes resets.
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
        // Stripe keeps fee VAT and withheld tax like the fees, so they reduce the payout too.
        var netRevenue = revenue - totalStripeFees - totalAppFees - totals.TotalFeeTax - totals.TotalWithheldTax;
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
                    Date = date.ToInvariantDate(),
                    TicketsSold = count,
                    RollingAverage = Math.Round(rollingAvg, 1),
                });
            }
        }

        var recentOrders = await ticketRepository.GetRecentOrdersAsync(count: 10);

        return new TicketDashboardStats
        {
            TicketsSold = ticketsSold,
            Revenue = revenue,
            TotalStripeFees = totalStripeFees,
            TotalApplicationFees = totalAppFees,
            TotalFeeTax = totals.TotalFeeTax,
            TotalWithheldTax = totals.TotalWithheldTax,
            NetRevenue = netRevenue,
            GrossAveragePrice = grossAvgPrice,
            UnmatchedOrderCount = unmatchedCount,
            FeesByPaymentMethod = feesByMethod,
            DailySalesPoints = dailySalesPoints,
            RecentOrders = recentOrders.ToList(),
            SyncStatus = syncState?.SyncStatus ?? TicketSyncStatus.Idle,
            SyncError = syncState?.LastError,
            LastSyncAt = syncState?.LastSyncAt,
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

    public async Task<CodeTrackingData> GetCodeTrackingDataAsync(string? search, CancellationToken ct = default)
    {
        var campaignData = await campaignService.GetCodeTrackingAsync(ct);

        var campaignSummaries = campaignData.Campaigns
            .Select(c => new CampaignCodeSummaryDto
            {
                CampaignId = c.CampaignId,
                CampaignTitle = c.CampaignTitle,
                CreatedAt = c.CreatedAt,
                TotalGrants = c.TotalGrants,
                Redeemed = c.Redeemed,
                Unused = c.TotalGrants - c.Redeemed,
                RedemptionRate = c.TotalGrants > 0
                    ? Math.Round(c.Redeemed * 100m / c.TotalGrants, 1)
                    : 0,
            })
            .ToList();

        IEnumerable<CampaignCodeTrackingGrant> allGrants = campaignData.Grants;
        var trimmed = search?.Trim();
        if (!string.IsNullOrEmpty(trimmed))
        {
            allGrants = allGrants.Where(g =>
                (g.Code?.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ?? false) ||
                g.RecipientName.Contains(trimmed, StringComparison.OrdinalIgnoreCase));
        }

        var ordersWithCodes = await ticketRepository.GetOrdersWithDiscountCodesAsync(ct);
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
                RecipientUserId = g.UserId,
                CampaignTitle = g.CampaignTitle,
                Status = g.RedeemedAt is not null ? "Redeemed" : "Pending",
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

    internal static readonly DateTimeZone MadridZone = DateTimeZoneProviders.Tzdb["Europe/Madrid"];

    /// <summary>
    /// Europe/Madrid, the zone the association books in, so a 00:30 UTC purchase on the 1st lands in the right month.
    /// </summary>
    private static (int Year, int Month) MonthKey(Instant purchasedAt)
    {
        var d = purchasedAt.InZone(MadridZone).Date;
        return (d.Year, d.Month);
    }

    public async Task<TicketSalesAggregates> GetSalesAggregatesAsync(CancellationToken ct = default)
    {
        var orders = await ticketRepository.GetPaidOrderSalesRowsAsync(ct);

        var weeklySales = orders
            .GroupBy(o =>
            {
                var date = o.PurchasedAt.InUtc().Date;
                var monday = date.PlusDays(-(int)date.DayOfWeek + 1);
                return monday;
            })
            .Select(g =>
            {
                var monday = g.Key;
                var sunday = monday.PlusDays(6);
                return new WeeklySalesAggregate
                {
                    WeekStart = monday,
                    WeekLabel = $"{monday.ToWeekdayDayMonth()} – {sunday.ToWeekdayDayMonth()}",
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

        var refunded = await ticketRepository.GetRefundedOrderRowsAsync(ct);
        var refundedByMonth = refunded
            .GroupBy(r => MonthKey(r.PurchasedAt))
            .ToDictionary(g => g.Key, g => g.ToList());
        var paidByMonth = orders.GroupBy(o => MonthKey(o.PurchasedAt)).ToDictionary(g => g.Key, g => g.ToList());
        var monthlySales = paidByMonth.Keys.Union(refundedByMonth.Keys)
            .Select(k =>
            {
                var paid = paidByMonth.GetValueOrDefault(k) ?? [];
                var refundedRows = refundedByMonth.GetValueOrDefault(k) ?? [];
                return new MonthlySalesAggregate
                {
                    MonthLabel = new YearMonth(k.Year, k.Month).ToInvariantYearMonth(),
                    OrderCount = paid.Count,
                    TicketsSold = paid.Sum(o => o.AttendeeCount),
                    GrossRevenue = paid.Sum(o => o.TotalAmount),
                    Donations = paid.Sum(o => o.DonationAmount),
                    VipDonations = paid.Sum(o => o.VipDonations),
                    VatAmount = paid.Sum(o => o.VatAmount),
                    // Built per order like ComputeOrderVat's base. TotalAmount can't stand in: a transfer voids a
                    // seat and adds a live replacement at the same price without changing the order total.
                    TicketIncomeInclVat = paid.Sum(o =>
                        Math.Max(0m, o.LiveSeatGross - o.VipDonations - o.DiscountAmount)),
                    // A refund returns the gross, never the processing fees already charged.
                    StripeFees = paid.Sum(o => o.StripeFee ?? 0m) + refundedRows.Sum(r => r.StripeFee ?? 0m),
                    ApplicationFees = paid.Sum(o => o.ApplicationFee ?? 0m)
                                      + refundedRows.Sum(r => r.ApplicationFee ?? 0m),
                    FeeTax = paid.Sum(o => o.FeeTax ?? 0m) + refundedRows.Sum(r => r.FeeTax ?? 0m),
                    WithheldTax = paid.Sum(o => o.WithheldTax ?? 0m) + refundedRows.Sum(r => r.WithheldTax ?? 0m),
                    RefundedGross = refundedRows.Sum(r => r.TotalAmount),
                };
            })
            .ToList();

        var attendees = await ticketRepository.GetPaidAttendeeTypePriceRowsAsync(ct);

        var byTicketType = attendees
            .GroupBy(a => (a.TicketTypeName, a.Price))
            .Select(g => new TicketTypeSalesAggregate
            {
                TicketTypeName = g.Key.TicketTypeName,
                Price = g.Key.Price,
                TicketsSold = g.Count(),
                FaceValue = g.Sum(a => a.Price),
            })
            .ToList();

        return new TicketSalesAggregates
        {
            WeeklySales = weeklySales,
            QuarterlySales = quarterlySales,
            MonthlySales = monthlySales,
            ByTicketType = byTicketType,
            ByDiscountCampaign = await BuildDiscountCampaignAggregatesAsync(ct),
        };
    }

    /// <summary>
    /// One row per campaign that granted codes, used or not, so the granted column totals org-wide. Redeemed codes
    /// with no matching grant collapse into one unattributed row.
    /// </summary>
    private async Task<List<DiscountCampaignAggregate>> BuildDiscountCampaignAggregatesAsync(CancellationToken ct)
    {
        var campaignData = await campaignService.GetCodeTrackingAsync(ct);
        var campaignIds = campaignData.Campaigns.Select(c => c.CampaignId).ToHashSet();

        var campaignByCode = campaignData.Grants
            .Where(g => !string.IsNullOrWhiteSpace(g.Code) && campaignIds.Contains(g.CampaignId))
            .GroupBy(g => g.Code!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().CampaignId, StringComparer.OrdinalIgnoreCase);

        var paidDiscountOrders = (await ticketRepository.GetOrdersWithDiscountCodesAsync(ct))
            .Where(o => o.IsPaid)
            .ToList();

        var usageByCampaign = paidDiscountOrders
            .Where(o => campaignByCode.ContainsKey(o.DiscountCode))
            .GroupBy(o => campaignByCode[o.DiscountCode])
            .ToDictionary(
                g => g.Key,
                g => (Used: g.Count(), Total: g.Sum(o => o.DiscountAmount ?? 0m)));

        var rows = campaignData.Campaigns
            .Where(c => c.TotalGrants > 0)
            .Select(c =>
            {
                usageByCampaign.TryGetValue(c.CampaignId, out var usage);
                return new DiscountCampaignAggregate
                {
                    CampaignTitle = c.CampaignTitle,
                    CreatedAt = c.CreatedAt,
                    CodesGranted = c.TotalGrants,
                    CodesUsed = usage.Used,
                    AverageDiscount = usage.Used > 0 ? Math.Round(usage.Total / usage.Used, 2) : 0m,
                    TotalDiscount = usage.Total,
                };
            })
            .ToList();

        var unattributed = paidDiscountOrders
            .Where(o => !campaignByCode.ContainsKey(o.DiscountCode))
            .ToList();
        if (unattributed.Count > 0)
        {
            var total = unattributed.Sum(o => o.DiscountAmount ?? 0m);
            rows.Add(new DiscountCampaignAggregate
            {
                CampaignTitle = "No campaign (vendor codes)",
                CodesGranted = null,
                CodesUsed = unattributed.Count,
                AverageDiscount = Math.Round(total / unattributed.Count, 2),
                TotalDiscount = total,
            });
        }

        return rows;
    }
}
