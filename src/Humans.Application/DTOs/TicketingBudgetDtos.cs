using NodaTime;

namespace Humans.Application.DTOs;

/// <summary>
/// A projected week of ticket sales — virtual line items not yet materialized.
/// </summary>
public class TicketingWeekProjection
{
    public required string WeekLabel { get; init; }
    public LocalDate WeekStart { get; init; }
    public LocalDate WeekEnd { get; init; }
    public int ProjectedTickets { get; init; }
    public decimal ProjectedRevenue { get; init; }
    public decimal ProjectedStripeFees { get; init; }
    public decimal ProjectedTtFees { get; init; }
}

/// <summary>
/// Aggregated ticket sales for a completed ISO week, used as input to
/// <c>IBudgetService.SyncTicketingActualsAsync</c>. Produced by the ticket
/// side (which owns TicketOrders) and consumed by the budget side (which
/// owns BudgetLineItems / TicketingProjections).
/// </summary>
public record TicketingWeeklyActuals(
    LocalDate Monday,
    LocalDate Sunday,
    string WeekLabel,
    int TicketCount,
    decimal Revenue,
    decimal StripeFees,
    decimal TicketTailorFees);

/// <summary>
/// A single paid ticket order summary — the primitive shape the
/// <c>ITicketingBudgetRepository</c> returns so <c>TicketingBudgetService</c>
/// can do the ISO-week bucketing in memory without holding EF types. The
/// <c>TicketCount</c> is pre-computed server-side from the order's attendees
/// (Valid + CheckedIn only).
/// </summary>
public record PaidTicketOrderSummary(
    Instant PurchasedAt,
    decimal TotalAmount,
    decimal? StripeFee,
    decimal? ApplicationFee,
    int TicketCount);
