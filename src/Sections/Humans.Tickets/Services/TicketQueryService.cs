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
    private readonly TicketPurchaseAudience purchaseAudience = new(
        ticketRepository, userService, userEmailService, teamService, governanceService, settingsService, participations);
    private readonly TicketSalesReports salesReports = new(ticketRepository, campaignService, budgetService, clock);
    private readonly UserTicketPersonalData personalData = new(ticketRepository, ticketTransferRepository, userService, settingsService, clock);

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

    public Task<TicketDashboardStats> GetDashboardStatsAsync() =>
        salesReports.GetDashboardStatsAsync();

    public Task<BreakEvenResult> CalculateBreakEvenAsync(
        int ticketsSold, decimal grossRevenue, string currency, bool canAccessFinance, int fallbackTarget) =>
        salesReports.CalculateBreakEvenAsync(ticketsSold, grossRevenue, currency, canAccessFinance, fallbackTarget);

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

    public Task<WhoHasntBoughtResult> GetWhoHasntBoughtAsync(
        string? search, string? filterTeam, string? filterTier, string? filterTicketStatus,
        int page, int pageSize, CancellationToken ct = default) =>
        purchaseAudience.GetWhoHasntBoughtAsync(search, filterTeam, filterTier, filterTicketStatus, page, pageSize, ct);

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
        personalData.GetUserTicketHoldingsAsync(userId, ct);

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

    public Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct) =>
        personalData.ContributeForUserAsync(userId, ct);

    internal const string TicketOrders = UserTicketPersonalData.TicketOrders;
    internal const string TicketAttendeeMatches = UserTicketPersonalData.TicketAttendeeMatches;

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
