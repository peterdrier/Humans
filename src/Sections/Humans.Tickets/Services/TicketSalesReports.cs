using Humans.Base.Extensions;
using Humans.Base.Interfaces;
using Humans.Campaigns.Contracts;
using Humans.Tickets.Data;
using Humans.Tickets.Services.Dtos;
using NodaTime;

namespace Humans.Tickets.Services;

internal sealed class TicketSalesReports(ITicketRepository ticketRepository, ICampaignService campaignService) : IApplicationService
{
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
