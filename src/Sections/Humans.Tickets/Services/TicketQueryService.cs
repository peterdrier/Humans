using Humans.AuditLog.Contracts;
using NodaTime;
using Humans.Base.Extensions;
using Humans.Base.Constants;
using Humans.Base.Enums;
using Humans.Budget.Contracts;
using Humans.Users.Contracts;
using Humans.Campaigns.Contracts;
using Humans.Settings.Contracts;
using Humans.Teams.Contracts;
using Humans.Tickets.Contracts;
using Humans.Tickets.Data;
using Humans.Tickets.Domain;
using Humans.Tickets.Services.Dtos;
using Humans.Gdpr.Contracts;
using Humans.Governance.Contracts;

namespace Humans.Tickets.Services;

/// <summary>
/// Tickets read service (inner) behind <c>CachingTicketQueryService</c> decorator (§15).
/// </summary>
internal sealed class TicketQueryService(
    ITicketRepository ticketRepository,
    ITicketTransferRepository ticketTransferRepository,
    IBudgetService budgetService,
    ICampaignService campaignService,
    IUserService userService,
    IUserEmailService userEmailService,
    ITeamService teamService,
    IGovernanceService governanceService,
    ISettingsService settingsService,
    ITicketCacheInvalidator cacheInvalidator,
    IAuditLogService auditLog,
    IClock clock,
    IEventParticipationManagement participations) : ITicketManagement, IUserDataContributor
{
    public Task<IReadOnlyList<EventParticipationInfo>> GetParticipationsAsync(Guid userId, CancellationToken ct = default) =>
        participations.GetParticipationsAsync(userId, ct);

    public Task<IReadOnlyList<EventParticipationInfo>> GetAllParticipationsAsync(CancellationToken ct = default) =>
        participations.GetAllParticipationsAsync(ct);

    private async Task<int> ComputeUserTicketCountAsync(Guid userId, CancellationToken ct)
    {
        // Attendees only — buyers don't count for themselves.
        var matchedCount = await ticketRepository
            .CountValidAttendeesMatchedToUserAsync(userId, ct);
        if (matchedCount > 0)
            return matchedCount;

        // Fallback: attendee emails against the same verified-email → user index the sync
        // matches with, so an alias verified by two users counts for neither, as in sync.
        var attendeeEmails = await ticketRepository.GetValidAttendeeEmailsAsync(ct);
        if (attendeeEmails.Count == 0)
            return 0;

        var (lookup, _) = VerifiedEmailLookup.Build(await userService.GetAllUserInfosAsync(ct));
        return attendeeEmails.Count(email => lookup.TryGetValue(email, out var owner) && owner == userId);
    }

    public async Task<IReadOnlyList<TicketOrderInfo>> GetTicketOrdersAsync(CancellationToken ct = default)
    {
        var syncState = await ticketRepository.GetSyncStateAsync(ct);
        var currentEventId = syncState?.VendorEventId;
        var orders = await ticketRepository.GetAllOrdersWithAttendeesAsync(ct);

        var approved = await ticketTransferRepository.GetByStatusAsync(TicketTransferStatus.Approved, ct);
        var transfersByAttendee = approved
            .GroupBy(t => t.OriginalTicketAttendeeId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(t => t.DecidedAt).First());

        return orders.Select(o => Project(o, currentEventId, transfersByAttendee)).ToList();
    }

    public async Task<TicketAttendeeInfo?> FindCurrentEventAttendeeByBarcodeAsync(
        string? barcode, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(barcode)) return null;
        return FindCurrentEventAttendeeByBarcode(await GetTicketOrdersAsync(ct), barcode);
    }

    internal static TicketAttendeeInfo? FindCurrentEventAttendeeByBarcode(
        IReadOnlyList<TicketOrderInfo> orders, string barcode)
    {
        var code = barcode.Trim();
        return orders.Where(o => o.IsCurrentEvent)
            .SelectMany(o => o.Attendees)
            .FirstOrDefault(a => string.Equals(a.Barcode, code, StringComparison.Ordinal));
    }

    public async Task<List<string>> GetAvailableTicketTypesAsync(CancellationToken ct = default)
    {
        var types = await ticketRepository.GetDistinctTicketTypesAsync(ct);
        return types.ToList();
    }

    private async Task<HashSet<Guid>> GetCurrentEventTicketHolderIdsAsync(CancellationToken ct)
    {
        var syncState = await ticketRepository.GetSyncStateAsync(ct);
        if (syncState is null || string.IsNullOrEmpty(syncState.VendorEventId))
            return [];

        var ids = await ticketRepository.GetEventTicketHolderUserIdsAsync(syncState.VendorEventId, ct);
        return ids.ToHashSet();
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
        // Fee VAT and withheld tax are kept by Stripe like the fees, so they reduce the payout too.
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

    private static readonly DateTimeZone MadridZone = DateTimeZoneProviders.Tzdb["Europe/Madrid"];

    /// <summary>
    /// Accounting month of a purchase, in Europe/Madrid — the zone the association books in,
    /// so a 00:30 UTC purchase on the 1st is not reported against the previous month.
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
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var monday = g.Key;
                var sunday = monday.PlusDays(6);
                return new WeeklySalesAggregate
                {
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

        var refunded = await ticketRepository.GetRefundedOrderRowsAsync(ct);
        var refundedByMonth = refunded
            .GroupBy(r => MonthKey(r.PurchasedAt))
            .ToDictionary(g => g.Key, g => g.ToList());
        var paidByMonth = orders.GroupBy(o => MonthKey(o.PurchasedAt)).ToDictionary(g => g.Key, g => g.ToList());
        var monthlySales = paidByMonth.Keys.Union(refundedByMonth.Keys)
            .OrderBy(k => k)
            .Select(k =>
            {
                var paid = paidByMonth.GetValueOrDefault(k) ?? [];
                var refundedRows = refundedByMonth.GetValueOrDefault(k) ?? [];
                return new MonthlySalesAggregate
                {
                    MonthLabel = $"{k.Year:D4}-{k.Month:D2}",
                    OrderCount = paid.Count,
                    TicketsSold = paid.Sum(o => o.AttendeeCount),
                    GrossRevenue = paid.Sum(o => o.TotalAmount),
                    Donations = paid.Sum(o => o.DonationAmount),
                    VipDonations = paid.Sum(o => o.VipDonations),
                    VatAmount = paid.Sum(o => o.VatAmount),
                    // Built over the live seats the way TicketSyncService.ComputeOrderVat builds
                    // the VAT base — Σ min(price, threshold) less the discount, floored per order.
                    // TotalAmount cannot stand in: a transfer voids a seat and adds a live
                    // replacement at the same price without changing the order total.
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
            .OrderByDescending(t => t.FaceValue)
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
    /// One row per campaign that granted codes, whether or not any were redeemed, so the
    /// granted column totals org-wide rather than over the campaigns that happen to have
    /// paid usage. Amounts come from the paid orders that carried each campaign's codes;
    /// redeemed codes with no matching grant collapse into one unattributed row.
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
                    CodesGranted = c.TotalGrants,
                    CodesUsed = usage.Used,
                    AverageDiscount = usage.Used > 0 ? Math.Round(usage.Total / usage.Used, 2) : 0m,
                    TotalDiscount = usage.Total,
                };
            })
            .OrderByDescending(r => r.TotalDiscount)
            .ThenBy(r => r.CampaignTitle, StringComparer.OrdinalIgnoreCase)
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

    public async Task<OrdersPageResult> GetOrdersPageAsync(
        string? search, string sortBy, bool sortDesc,
        int page, int pageSize,
        string? filterPaymentStatus, string? filterTicketType, bool? filterMatched, CancellationToken ct = default)
    {
        TicketPaymentStatus? parsedStatus = null;
        if (!string.IsNullOrEmpty(filterPaymentStatus) &&
            Enum.TryParse<TicketPaymentStatus>(filterPaymentStatus, true, out var paymentStatus))
        {
            parsedStatus = paymentStatus;
        }

        var (rawRows, totalCount) = await ticketRepository.GetOrdersPageAsync(
            search, sortBy, sortDesc, page, pageSize,
            parsedStatus, filterTicketType, filterMatched, ct);

        var rows = await HydrateOrderMatchedUserNamesAsync(rawRows, ct);
        return new OrdersPageResult { Rows = rows, TotalCount = totalCount };
    }

    public async Task<AttendeesPageResult> GetAttendeesPageAsync(
        string? search, string sortBy, bool sortDesc,
        int page, int pageSize,
        string? filterTicketType, string? filterStatus, bool? filterMatched, string? filterOrderId,
        bool filterMultipleTickets = false, CancellationToken ct = default)
    {
        TicketAttendeeStatus? parsedStatus = null;
        if (!string.IsNullOrEmpty(filterStatus) &&
            Enum.TryParse<TicketAttendeeStatus>(filterStatus, true, out var status))
        {
            parsedStatus = status;
        }

        var (rawRows, totalCount) = await ticketRepository.GetAttendeesPageAsync(
            search, sortBy, sortDesc, page, pageSize,
            filterTicketType, parsedStatus, filterMatched, filterOrderId, filterMultipleTickets, ct);

        var rows = await HydrateAttendeeMatchedUserNamesAsync(rawRows, ct);
        return new AttendeesPageResult { Rows = rows, TotalCount = totalCount };
    }

    private async Task<List<OrderRow>> HydrateOrderMatchedUserNamesAsync(IReadOnlyList<OrderRow> rows, CancellationToken ct)
    {
        var matchedIds = rows
            .Where(r => r.MatchedUserId is not null)
            .Select(r => r.MatchedUserId!.Value)
            .Distinct()
            .ToList();
        if (matchedIds.Count == 0)
            return rows.ToList();

        var users = await userService.GetUserInfosAsync(matchedIds, ct);
        return rows.Select(row =>
            row.MatchedUserId is { } userId && users.TryGetValue(userId, out var user)
                ? row with { MatchedUserName = user.BurnerName }
                : row).ToList();
    }

    private async Task<List<AttendeeRow>> HydrateAttendeeMatchedUserNamesAsync(IReadOnlyList<AttendeeRow> rows, CancellationToken ct)
    {
        var matchedIds = rows
            .Where(r => r.MatchedUserId is not null)
            .Select(r => r.MatchedUserId!.Value)
            .Distinct()
            .ToList();
        if (matchedIds.Count == 0)
            return rows.ToList();

        var users = await userService.GetUserInfosAsync(matchedIds, ct);
        return rows.Select(row =>
            row.MatchedUserId is { } userId && users.TryGetValue(userId, out var user)
                ? row with { MatchedUserName = user.BurnerName }
                : row).ToList();
    }

    public async Task<WhoHasntBoughtResult> GetWhoHasntBoughtAsync(
        string? search, string? filterTeam, string? filterTier, string? filterTicketStatus,
        int page, int pageSize, CancellationToken ct = default)
    {
        var matchedUserIds = await GetCurrentEventTicketHolderIdsAsync(ct);

        var allUsers = await userService.GetAllUserInfosAsync(ct).ConfigureAwait(false);
        var volunteerTeam = await teamService.GetTeamAsync(SystemTeamIds.Volunteers, ct);
        var volunteerUserIds = volunteerTeam?.Members.Select(m => m.UserId).ToHashSet() ?? [];

        var candidateIds = allUsers
            .Where(u => volunteerUserIds.Contains(u.Id))
            .Select(u => u.Id)
            .ToList();

        var activeEvent = await settingsService.GetActiveEventSettingsAsync(ct);
        HashSet<Guid> notAttendingSet = [];
        if (activeEvent is not null && activeEvent.Year > 0)
        {
            notAttendingSet = (await participations.GetAllParticipationsAsync(ct))
                .Where(ep => ep.Year == activeEvent.Year && ep.Status == ParticipationStatus.NotAttending)
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

        var emailsById = await userEmailService.GetNotificationEmailsByUserIdsAsync(candidateIds, ct);
        var candidateIdSet = candidateIds.ToHashSet();
        var teamsByIdLookup = await teamService.GetTeamsAsync(ct);
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
        var tiers = await governanceService.GetMembershipTiersAsync(ct);

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
                    Tier = tiers.GetValueOrDefault(id, MembershipTier.Volunteer),
                };
            })
            .ToList();

        // Search the full verified-email set so admins can find secondary-address purchases.
        // Matched in-memory against the verified emails already carried on each loaded
        // UserInfo — no extra DB round-trip.
        HashSet<Guid>? emailMatchUserIds = null;
        if (search.HasSearchTerm(1))
        {
            var term = search.Trim();
            emailMatchUserIds = allUsers
                .Where(u => u.UserEmails.Any(e => e.IsVerified && e.Email.ContainsOrdinalIgnoreCase(term)))
                .Select(u => u.Id)
                .ToHashSet();
        }

        var filtered = FilterWhoHasntBoughtRows(
            rows, filterTicketStatus, filterTeam, filterTier, search, emailMatchUserIds);

        var totalCount = filtered.Count;
        var pagedHumans = filtered
            .Skip((int)Math.Clamp(((long)page - 1) * pageSize, 0, int.MaxValue))
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

        if (search.HasSearchTerm(1))
        {
            filtered = filtered.Where(r =>
                r.Name.ContainsOrdinalIgnoreCase(search) ||
                r.Email.ContainsOrdinalIgnoreCase(search) ||
                (emailMatchUserIds is not null && emailMatchUserIds.Contains(r.UserId)));
        }

        return filtered.ToList();
    }

    public async Task<List<AttendeeExportRow>> GetAttendeeExportDataAsync(Guid actorUserId, CancellationToken ct = default)
    {
        var rows = await ticketRepository.GetAttendeeExportDataAsync(ct);
        await auditLog.LogAsync(AuditAction.TicketAttendeesExported, "Tickets", Guid.Empty,
            $"Attendee list exported: {rows.Count} rows", actorUserId);
        return rows.ToList();
    }

    public async Task<List<OrderExportRow>> GetOrderExportDataAsync(Guid actorUserId, CancellationToken ct = default)
    {
        var rows = await ticketRepository.GetOrderExportDataAsync(ct);
        await auditLog.LogAsync(AuditAction.TicketOrdersExported, "Tickets", Guid.Empty,
            $"Order list exported: {rows.Count} rows", actorUserId);
        return rows.ToList();
    }

    public async Task<List<DonationExportRow>> GetDonationExportDataAsync(Guid actorUserId, CancellationToken ct = default)
    {
        var orders = (await ticketRepository.GetOrderExportDataAsync(ct))
            .Where(o => string.Equals(o.PaymentStatus, nameof(TicketPaymentStatus.Paid), StringComparison.Ordinal))
            .OrderBy(o => o.PurchasedAt);

        var rows = new List<DonationExportRow>();
        foreach (var o in orders)
        {
            var date = o.PurchasedAt.InZone(MadridZone).Date.ToInvariantDate();
            if (o.VipDonations > 0)
                rows.Add(new DonationExportRow(date, o.VendorOrderId, o.BuyerName, o.BuyerEmail, DonationExportRow.VipTicket, o.VipDonations));
            if (o.DonationAmount > 0)
                rows.Add(new DonationExportRow(date, o.VendorOrderId, o.BuyerName, o.BuyerEmail, DonationExportRow.SeparateDonation, o.DonationAmount));
        }

        await auditLog.LogAsync(
            AuditAction.TicketDonationsExported,
            "Tickets", Guid.Empty,
            description: $"Donor list exported: {rows.Count} donation rows, {rows.Sum(r => r.Amount):F2} EUR",
            actorUserId: actorUserId);

        return rows;
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
        // GroupBy defensive: stray duplicate pendings must not crash dashboard read.
        var pendingByAttendee = (await ticketTransferRepository.GetBySenderAsync(userId, ct))
            .Where(r => r.Status == TicketTransferStatus.Pending)
            .GroupBy(r => r.OriginalTicketAttendeeId)
            .ToDictionary(g => g.Key, g => g.First().Id);
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
                a.Status,
                HasPendingOutgoingTransfer: pendingByAttendee.ContainsKey(a.Id),
                PendingTransferRequestId: pendingByAttendee.TryGetValue(a.Id, out var transferId)
                    ? transferId
                    : null,
                CheckedInAt: a.CheckedInAt))
            .ToList();

        var ticketCount = await ComputeUserTicketCountAsync(userId, ct);
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

    private async Task<Instant?> GetPostEventHoldDateAsync(CancellationToken ct)
    {
        var activeEvent = await settingsService.GetActiveEventSettingsAsync(ct);
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

    private async Task<UserTicketExportData> GetUserTicketExportDataAsync(
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

    private static TicketOrderInfo Project(
        TicketOrder o, string? currentEventId,
        IReadOnlyDictionary<Guid, TicketTransferRequest> transfersByAttendee) => new(
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
        Attendees: o.Attendees.Select(a =>
        {
            transfersByAttendee.TryGetValue(a.Id, out var tr);
            var hasTransfer = a.Status == TicketAttendeeStatus.Void && tr is not null;
            return new TicketAttendeeInfo(
                Id: a.Id,
                VendorTicketId: a.VendorTicketId,
                AttendeeName: a.AttendeeName,
                AttendeeEmail: a.AttendeeEmail,
                TicketTypeName: a.TicketTypeName,
                Price: a.Price,
                Status: a.Status,
                MatchedUserId: a.MatchedUserId,
                Barcode: a.Barcode,
                TransferredToName: hasTransfer ? tr!.ReceiverLegalName : null,
                TransferredAt: hasTransfer ? tr!.DecidedAt : null,
                CheckedInAt: a.CheckedInAt);
        }).ToList(),
        StripeFee: o.StripeFee,
        ApplicationFee: o.ApplicationFee,
        FeeTax: o.FeeTax,
        WithheldTax: o.WithheldTax);

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(
        Guid userId, CancellationToken ct)
    {
        var export = await GetUserTicketExportDataAsync(userId, ct);

        var ordersSlice = new UserDataSlice(TicketOrders, export.Orders.Select(o => new
        {
            o.BuyerName,
            o.BuyerEmail,
            o.TotalAmount,
            o.Currency,
            o.PaymentStatus,
            o.DiscountCode,
            PurchasedAt = o.PurchasedAt.ToIso8601(),
        }).ToList());

        var attendeesSlice = new UserDataSlice(TicketAttendeeMatches, export.Attendees.Select(a => new
        {
            a.AttendeeName,
            a.AttendeeEmail,
            a.TicketTypeName,
            a.Price,
            a.Status,
        }).ToList());

        return [ordersSlice, attendeesSlice];
    }

    internal const string TicketOrders = "TicketOrders";
    internal const string TicketAttendeeMatches = "TicketAttendeeMatches";

    private const string SalesRecordRetention =
        "Partially retained: the order/attendee row itself (amount, currency, ticket type, " +
        "payment status, dates) is a sales record backing the association's books — Código de " +
        "Comercio Art. 30 and Ley 58/2003 Art. 66, GDPR Art. 17(3)(b). Every identifier on it " +
        "is erased: BuyerName/BuyerEmail and AttendeeName/AttendeeEmail are overwritten with " +
        "tombstones, MatchedUserId is dropped, and the free text on their ticket-transfer " +
        "requests is cleared.";

    private static readonly IReadOnlyDictionary<string, string?> Erasure =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [TicketOrders] = SalesRecordRetention,
            [TicketAttendeeMatches] = SalesRecordRetention
        };

    public IReadOnlyDictionary<string, string?> ErasureDeclaration => Erasure;

    public async Task EraseForUserAsync(Guid userId, CancellationToken ct)
    {
        await ticketRepository.EraseUserPiiAsync(userId, ct);
        await ticketTransferRepository.ErasePiiForUserAsync(userId, ct);

        // The order projection is a warmed singleton with no TTL and carries the
        // buyer/attendee names and emails just tombstoned — without this the erased
        // identity keeps being served from memory until the process restarts.
        cacheInvalidator.InvalidateAll();
    }

    public Task<IReadOnlyList<OrderDriftRow>> GetOrderDriftAsync(CancellationToken ct = default) =>
        ticketRepository.GetOrderDriftAsync(ct);
}
