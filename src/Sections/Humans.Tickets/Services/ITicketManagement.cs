using Humans.Base.Interfaces;
using Humans.Gdpr.Contracts;
using Humans.Tickets.Contracts;
using Humans.Tickets.Services.Dtos;
using NodaTime;

namespace Humans.Tickets.Services;

/// <summary>Tickets-internal; other sections depend on <see cref="ITicketService"/>.</summary>
internal interface ITicketManagement : ITicketService, IApplicationService
{
    Task<TicketDashboardStats> GetDashboardStatsAsync();

    Task<BreakEvenResult> CalculateBreakEvenAsync(
        int ticketsSold,
        decimal grossRevenue,
        string currency,
        bool canAccessFinance,
        int fallbackTarget);

    Task<TicketSalesAggregates> GetSalesAggregatesAsync(CancellationToken ct = default);

    Task<List<string>> GetAvailableTicketTypesAsync(CancellationToken ct = default);

    Task<CodeTrackingData> GetCodeTrackingDataAsync(string? search, CancellationToken ct = default);

    Task<OrdersPageResult> GetOrdersPageAsync(
        string? search, string sortBy, bool sortDesc,
        int page, int pageSize,
        string? filterPaymentStatus, string? filterTicketType, bool? filterMatched, CancellationToken ct = default);

    Task<AttendeesPageResult> GetAttendeesPageAsync(
        string? search, string sortBy, bool sortDesc,
        int page, int pageSize,
        string? filterTicketType, string? filterStatus, bool? filterMatched, string? filterOrderId,
        bool filterMultipleTickets = false, CancellationToken ct = default);

    Task<WhoHasntBoughtResult> GetWhoHasntBoughtAsync(
        string? search, string? filterTeam, string? filterTier, string? filterTicketStatus,
        int page, int pageSize, CancellationToken ct = default);

    /// <summary>Unordered; the export controller orders them.</summary>
    Task<List<AttendeeExportRow>> GetAttendeeExportDataAsync(Guid actorUserId, CancellationToken ct = default);

    /// <summary>Unordered; the export controller orders them.</summary>
    Task<List<OrderExportRow>> GetOrderExportDataAsync(Guid actorUserId, CancellationToken ct = default);

    /// <summary>
    /// Oldest first, dated in Europe/Madrid. Audits the actor: this is a bulk export of buyer identity leaving the
    /// system.
    /// </summary>
    Task<List<DonationExportRow>> GetDonationExportDataAsync(Guid actorUserId, CancellationToken ct = default);

    Task<IReadOnlyList<OrderDriftRow>> GetOrderDriftAsync(CancellationToken ct = default);

    Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct);

    Task EraseForUserAsync(Guid userId, CancellationToken ct);
}

internal sealed record UserTicketExportData(
    IReadOnlyList<UserTicketOrderExportRow> Orders,
    IReadOnlyList<UserTicketAttendeeExportRow> Attendees);

internal sealed record UserTicketOrderExportRow(
    string? BuyerName,
    string? BuyerEmail,
    decimal TotalAmount,
    string Currency,
    string PaymentStatus,
    string? DiscountCode,
    Instant PurchasedAt);

internal sealed record UserTicketAttendeeExportRow(
    string? AttendeeName,
    string? AttendeeEmail,
    string? TicketTypeName,
    decimal Price,
    string Status);

internal sealed record OrderDriftRow(
    Guid OrderId,
    string VendorOrderId,
    string BuyerName,
    int IssuedCount,
    int ValidCount,
    string? VendorDashboardUrl);
