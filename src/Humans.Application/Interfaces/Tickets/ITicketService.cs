using Humans.Application.Architecture;
using Humans.Application.DTOs;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.Interfaces.Tickets;

/// <summary>
/// Cross-section read surface for Tickets. External sections inject this
/// interface; it exposes only DTO/read-model projections, primitives, NodaTime,
/// and collections. It must not expose EF entity types.
/// </summary>
[SurfaceBudget(2)]
public interface ITicketServiceRead
{
    /// <summary>
    /// Returns the ticket order projection used by cross-section read callers.
    /// Callers derive aggregate questions from this DTO instead of adding
    /// one-off service methods.
    /// </summary>
    Task<IReadOnlyList<TicketOrderInfo>> GetTicketOrdersAsync(CancellationToken ct = default);

    /// <summary>
    /// Snapshot of a user's ticket holdings: count of orders where they're the
    /// buyer, plus the attendee names of every ticket where they are the
    /// current owner.
    /// </summary>
    Task<UserTicketHoldings> GetUserTicketHoldingsAsync(Guid userId, CancellationToken ct = default);
}

/// <summary>
/// Full Tickets service surface for Tickets-owned admin/query workflows.
/// External sections should depend on <see cref="ITicketServiceRead"/>.
/// </summary>
public interface ITicketService : ITicketServiceRead, IApplicationService
{
    /// <summary>
    /// Compute aggregated dashboard statistics: revenue, fees, daily sales,
    /// recent orders, volunteer coverage, and sync state.
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
    Task<TicketSalesAggregates> GetSalesAggregatesAsync();

    /// <summary>
    /// Get the distinct ticket type names across all attendees.
    /// </summary>
    Task<List<string>> GetAvailableTicketTypesAsync();

    /// <summary>
    /// Get code tracking data: campaign summaries and individual code details
    /// with redemption status. Optionally filters codes by search term.
    /// </summary>
    Task<CodeTrackingData> GetCodeTrackingDataAsync(string? search);

    /// <summary>
    /// Get a paged list of orders with filtering and sorting.
    /// </summary>
    Task<OrdersPageResult> GetOrdersPageAsync(
        string? search, string sortBy, bool sortDesc,
        int page, int pageSize,
        string? filterPaymentStatus, string? filterTicketType, bool? filterMatched);

    /// <summary>
    /// Get a paged list of attendees with filtering and sorting.
    /// </summary>
    Task<AttendeesPageResult> GetAttendeesPageAsync(
        string? search, string sortBy, bool sortDesc,
        int page, int pageSize,
        string? filterTicketType, string? filterStatus, bool? filterMatched, string? filterOrderId,
        bool filterMultipleTickets = false);

    /// <summary>
    /// Get data for the "who hasn't bought" page: all active humans with ticket
    /// match status, filtered and paged.
    /// </summary>
    Task<WhoHasntBoughtResult> GetWhoHasntBoughtAsync(
        string? search, string? filterTeam, string? filterTier, string? filterTicketStatus,
        int page, int pageSize);

    /// <summary>
    /// Get all attendees for CSV export, ordered by name.
    /// </summary>
    Task<List<AttendeeExportRow>> GetAttendeeExportDataAsync();

    /// <summary>
    /// Get all orders for CSV export, ordered by purchase date descending.
    /// </summary>
    Task<List<OrderExportRow>> GetOrderExportDataAsync();

    /// <summary>
    /// Returns ticket data for a user's GDPR data export.
    /// </summary>
    Task<UserTicketExportData> GetUserTicketExportDataAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Returns paid orders where the number of valid+checked-in attendees is
    /// less than the total number of attendees on the order.
    /// </summary>
    Task<IReadOnlyList<OrderDriftRow>> GetOrderDriftAsync(CancellationToken ct = default);
}

/// <summary>
/// Ticket data for a user's GDPR data export.
/// </summary>
public record UserTicketExportData(
    IReadOnlyList<UserTicketOrderExportRow> Orders,
    IReadOnlyList<UserTicketAttendeeExportRow> Attendees);

/// <summary>
/// A single ticket order row in the user data export.
/// </summary>
public record UserTicketOrderExportRow(
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
public record UserTicketAttendeeExportRow(
    string? AttendeeName,
    string? AttendeeEmail,
    string? TicketTypeName,
    decimal Price,
    string Status);

/// <summary>
/// Summary of a ticket order for display on user-facing pages.
/// </summary>
public record UserTicketOrderSummary(
    string BuyerName,
    Instant PurchasedAt,
    int AttendeeCount,
    decimal TotalAmount,
    string Currency);

/// <summary>
/// Holdings summary for a single user: orders they bought plus per-ticket rows
/// for every ticket they currently hold.
/// </summary>
public record UserTicketHoldings(
    int OrderCount,
    IReadOnlyList<UserTicketHoldingRow> Tickets,
    bool HasCurrentEventTicket = false,
    int TicketCount = 0,
    Instant? PostEventHoldDate = null)
{
    public IReadOnlyList<UserTicketOrderSummary> OrderSummaries { get; init; } =
        Array.Empty<UserTicketOrderSummary>();

    public IReadOnlyList<Guid> OpenTicketOrderIds { get; init; } =
        Array.Empty<Guid>();

    public bool HasTicketAttendeeMatch => OrderCount > 0 || Tickets.Count > 0;
}

/// <summary>
/// One ticket held by a user, with enough info for the holdings widget to render.
/// </summary>
public record UserTicketHoldingRow(
    Guid AttendeeId,
    string AttendeeName,
    string? AttendeeEmail,
    string VendorTicketId,
    string TicketTypeName,
    TicketAttendeeStatus Status);

/// <summary>
/// A single row in the order-drift diagnostic.
/// </summary>
public sealed record OrderDriftRow(
    Guid OrderId,
    string VendorOrderId,
    string BuyerName,
    int IssuedCount,
    int ValidCount,
    string? VendorDashboardUrl);
