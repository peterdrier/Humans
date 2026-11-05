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
    IAuditLogService auditLog,
    IClock clock,
    IEventParticipationManagement participations) : ITicketManagement
{
    private readonly TicketSalesReports salesReports = new(ticketRepository, campaignService);
    private readonly UserTicketHoldingsReader holdingsReader = new(ticketRepository, ticketTransferRepository, userService, settingsService, clock);

    public Task<IReadOnlyList<EventParticipationInfo>> GetParticipationsAsync(Guid userId, CancellationToken ct = default) =>
        participations.GetParticipationsAsync(userId, ct);

    public Task<IReadOnlyList<EventParticipationInfo>> GetAllParticipationsAsync(CancellationToken ct = default) =>
        participations.GetAllParticipationsAsync(ct);

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

    public Task<CodeTrackingData> GetCodeTrackingDataAsync(string? search, CancellationToken ct = default) =>
        salesReports.GetCodeTrackingDataAsync(search, ct);

    public Task<TicketSalesAggregates> GetSalesAggregatesAsync(CancellationToken ct = default) =>
        salesReports.GetSalesAggregatesAsync(ct);

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

        var (rows, totalCount) = await ticketRepository.GetOrdersPageAsync(
            search, sortBy, sortDesc, page, pageSize,
            parsedStatus, filterTicketType, filterMatched, ct);

        return new OrdersPageResult { Rows = rows.ToList(), TotalCount = totalCount };
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

        var (rows, totalCount) = await ticketRepository.GetAttendeesPageAsync(
            search, sortBy, sortDesc, page, pageSize,
            filterTicketType, parsedStatus, filterMatched, filterOrderId, filterMultipleTickets, ct);

        return new AttendeesPageResult { Rows = rows.ToList(), TotalCount = totalCount };
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

        // Search all verified emails so admins can find secondary-address purchases.
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
                Email = r.Email,
                Teams = string.Join(", ", r.TeamNames),
                Tier = r.Tier,
            })
            .ToList();

        var availableTeams = rows
            .SelectMany(r => r.TeamNames)
            .Distinct(StringComparer.OrdinalIgnoreCase)
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
        await auditLog.LogAsync(TicketsAuditActions.TicketAttendeesExported, "Tickets", Guid.Empty,
            $"Attendee list exported: {rows.Count} rows", actorUserId);
        return rows.ToList();
    }

    public async Task<List<OrderExportRow>> GetOrderExportDataAsync(Guid actorUserId, CancellationToken ct = default)
    {
        var rows = await ticketRepository.GetOrderExportDataAsync(ct);
        await auditLog.LogAsync(TicketsAuditActions.TicketOrdersExported, "Tickets", Guid.Empty,
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
            var date = o.PurchasedAt.InZone(TicketSalesReports.MadridZone).Date.ToInvariantDate();
            if (o.VipDonations > 0)
                rows.Add(new DonationExportRow(date, o.VendorOrderId, o.BuyerName, o.BuyerEmail, DonationExportRow.VipTicket, o.VipDonations));
            if (o.DonationAmount > 0)
                rows.Add(new DonationExportRow(date, o.VendorOrderId, o.BuyerName, o.BuyerEmail, DonationExportRow.SeparateDonation, o.DonationAmount));
        }

        await auditLog.LogAsync(
            TicketsAuditActions.TicketDonationsExported,
            "Tickets", Guid.Empty,
            description: $"Donor list exported: {rows.Count} donation rows, {rows.Sum(r => r.Amount):F2} EUR",
            actorUserId: actorUserId);

        return rows;
    }

    public Task<UserTicketHoldings> GetUserTicketHoldingsAsync(Guid userId, CancellationToken ct = default) =>
        holdingsReader.GetUserTicketHoldingsAsync(userId, ct);

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

    internal static readonly IReadOnlyDictionary<string, string?> Erasure =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [TicketOrders] = SalesRecordRetention,
            [TicketAttendeeMatches] = SalesRecordRetention
        };

    public async Task EraseForUserAsync(Guid userId, CancellationToken ct)
    {
        await ticketRepository.EraseUserPiiAsync(userId, ct);
        await ticketTransferRepository.ErasePiiForUserAsync(userId, ct);
    }

    public Task<IReadOnlyList<OrderDriftRow>> GetOrderDriftAsync(CancellationToken ct = default) =>
        ticketRepository.GetOrderDriftAsync(ct);
}
