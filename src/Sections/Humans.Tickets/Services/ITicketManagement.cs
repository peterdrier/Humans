using Humans.Base.Interfaces;
using Humans.Gdpr.Contracts;
using Humans.Tickets.Contracts;
using Humans.Tickets.Services.Dtos;
using NodaTime;

namespace Humans.Tickets.Services;

/// <summary>
/// Full Tickets service surface for Tickets-owned admin/query workflows.
/// External sections should depend on <see cref="ITicketService"/>.
/// </summary>
internal interface ITicketManagement : ITicketService, IApplicationService
{
    /// <summary>
    /// Compute aggregated dashboard statistics: revenue, fees, daily sales,
    /// recent orders, and sync state.
    /// </summary>
    Task<TicketDashboardStats> GetDashboardStatsAsync();

    /// <summary>
    /// Calculate break-even target using gross average ticket price and planned expenses.
    /// </summary>
    Task<BreakEvenResult> CalculateBreakEvenAsync(
        int ticketsSold,
        decimal grossRevenue,
        string currency,
        bool canAccessFinance,
        int fallbackTarget);

    /// <summary>
    /// Compute weekly and quarterly sales aggregates for reporting.
    /// </summary>
    Task<TicketSalesAggregates> GetSalesAggregatesAsync(CancellationToken ct = default);

    /// <summary>
    /// Get the distinct ticket type names across all attendees.
    /// </summary>
    Task<List<string>> GetAvailableTicketTypesAsync(CancellationToken ct = default);

    /// <summary>
    /// Get code tracking data: campaign summaries and individual code details
    /// with redemption status. Optionally filters codes by search term.
    /// </summary>
    Task<CodeTrackingData> GetCodeTrackingDataAsync(string? search, CancellationToken ct = default);

    /// <summary>
    /// Get a paged list of orders with filtering and sorting.
    /// </summary>
    Task<OrdersPageResult> GetOrdersPageAsync(
        string? search, string sortBy, bool sortDesc,
        int page, int pageSize,
        string? filterPaymentStatus, string? filterTicketType, bool? filterMatched, CancellationToken ct = default);

    /// <summary>
    /// Get a paged list of attendees with filtering and sorting.
    /// </summary>
    Task<AttendeesPageResult> GetAttendeesPageAsync(
        string? search, string sortBy, bool sortDesc,
        int page, int pageSize,
        string? filterTicketType, string? filterStatus, bool? filterMatched, string? filterOrderId,
        bool filterMultipleTickets = false, CancellationToken ct = default);

    /// <summary>
    /// Get data for the "who hasn't bought" page: all active humans with ticket
    /// match status, filtered and paged.
    /// </summary>
    Task<WhoHasntBoughtResult> GetWhoHasntBoughtAsync(
        string? search, string? filterTeam, string? filterTier, string? filterTicketStatus,
        int page, int pageSize, CancellationToken ct = default);

    /// <summary>
    /// Get all attendees for CSV export, ordered by name.
    /// </summary>
    Task<List<AttendeeExportRow>> GetAttendeeExportDataAsync(Guid actorUserId, CancellationToken ct = default);

    /// <summary>
    /// Get all orders for CSV export, ordered by purchase date descending.
    /// </summary>
    Task<List<OrderExportRow>> GetOrderExportDataAsync(Guid actorUserId, CancellationToken ct = default);

    /// <summary>
    /// One row per donation on a paid order (VIP amount above the threshold, separate checkout
    /// donation), oldest first, dated in Europe/Madrid, for the accountant's donor list. Writes
    /// an audit entry for the actor: this is a bulk export of buyer identity leaving the system.
    /// </summary>
    Task<List<DonationExportRow>> GetDonationExportDataAsync(Guid actorUserId, CancellationToken ct = default);

    /// <summary>
    /// Returns paid orders where the number of valid+checked-in attendees is
    /// less than the total number of attendees on the order.
    /// </summary>
    Task<IReadOnlyList<OrderDriftRow>> GetOrderDriftAsync(CancellationToken ct = default);

    /// <summary>
    /// GDPR export slices for <paramref name="userId"/>. The caching decorator fronts this
    /// as the section's <c>IUserDataContributor</c>.
    /// </summary>
    Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct);

    /// <summary>
    /// GDPR erasure: tombstones the user's ticket PII. The caching decorator fronts this as
    /// the section's <c>IUserDataContributor</c> and evicts the slices that carried it.
    /// </summary>
    Task EraseForUserAsync(Guid userId, CancellationToken ct);
}

/// <summary>
/// Ticket data for a user's GDPR data export.
/// </summary>
internal sealed record UserTicketExportData(
    IReadOnlyList<UserTicketOrderExportRow> Orders,
    IReadOnlyList<UserTicketAttendeeExportRow> Attendees);

/// <summary>
/// A single ticket order row in the user data export.
/// </summary>
internal sealed record UserTicketOrderExportRow(
    string? BuyerName,
    string? BuyerEmail,
    decimal TotalAmount,
    string Currency,
    string PaymentStatus,
    string? DiscountCode,
    Instant PurchasedAt);

/// <summary>
/// A single ticket attendee row in the user data export.
/// </summary>
internal sealed record UserTicketAttendeeExportRow(
    string? AttendeeName,
    string? AttendeeEmail,
    string? TicketTypeName,
    decimal Price,
    string Status);

/// <summary>
/// A single row in the order-drift diagnostic.
/// </summary>
internal sealed record OrderDriftRow(
    Guid OrderId,
    string VendorOrderId,
    string BuyerName,
    int IssuedCount,
    int ValidCount,
    string? VendorDashboardUrl);
