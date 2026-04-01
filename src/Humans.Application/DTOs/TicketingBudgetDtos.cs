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
