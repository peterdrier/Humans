using NodaTime;

namespace Humans.Budget.Domain;

/// <summary>
/// Projection parameters for a ticketing budget group.
/// Configures revenue, fee, and VAT projections for ticket sales.
/// One-to-one with BudgetGroup where IsTicketingGroup = true.
/// </summary>
internal sealed class TicketingProjection
{
    public Guid Id { get; init; }
    public Guid BudgetGroupId { get; init; }
    public BudgetGroup? BudgetGroup { get; set; }

    /// <summary>First day of ticket sales.</summary>
    public LocalDate? StartDate { get; set; }

    /// <summary>Event date (end of sales period). Projections run from StartDate to EventDate.</summary>
    public LocalDate? EventDate { get; set; }

    /// <summary>Pre-sale / first-day burst ticket count.</summary>
    public int InitialSalesCount { get; set; }

    /// <summary>Projected tickets sold per day after initial burst.</summary>
    public decimal DailySalesRate { get; set; }

    /// <summary>Average ticket price in euros (gross, VAT-inclusive).</summary>
    public decimal AverageTicketPrice { get; set; }

    /// <summary>VAT rate percentage on ticket revenue (typically 10% in Spain).</summary>
    public int VatRate { get; set; }

    /// <summary>Stripe percentage fee (e.g. 1.5 for 1.5%).</summary>
    public decimal StripeFeePercent { get; set; }

    /// <summary>Stripe fixed fee per transaction in euros (e.g. 0.25).</summary>
    public decimal StripeFeeFixed { get; set; }

    /// <summary>TicketTailor percentage fee (e.g. 3.0 for 3%).</summary>
    public decimal TicketTailorFeePercent { get; set; }

    public Instant CreatedAt { get; init; }
    public Instant UpdatedAt { get; set; }

    // Preview and persisted budget lines share the same sales and fee policy.
    internal IEnumerable<(LocalDate Start, LocalDate End, int Tickets, decimal Revenue,
        decimal StripeFees, decimal TicketTailorFees)> CalculateWeeks(LocalDate today)
    {
        if (StartDate is null || EventDate is null || AverageTicketPrice == 0 || today > EventDate.Value)
            yield break;

        var currentMonday = today.PlusDays(1 - (int)today.DayOfWeek);
        var weekStart = currentMonday > StartDate.Value
            ? currentMonday
            : StartDate.Value.PlusDays(1 - (int)StartDate.Value.DayOfWeek);
        var isFirstWeek = true;

        while (weekStart <= EventDate.Value)
        {
            var weekEnd = weekStart.PlusDays(6);
            if (weekEnd > EventDate.Value) weekEnd = EventDate.Value;

            var days = Period.Between(weekStart, weekEnd.PlusDays(1), PeriodUnits.Days).Days;
            var tickets = (int)Math.Round(DailySalesRate * days);
            if (isFirstWeek && weekStart <= StartDate.Value)
                tickets += InitialSalesCount;
            isFirstWeek = false;
            if (tickets <= 0) tickets = 1;

            var revenue = tickets * AverageTicketPrice;
            yield return (weekStart, weekEnd, tickets, revenue,
                revenue * StripeFeePercent / 100m + tickets * StripeFeeFixed,
                revenue * TicketTailorFeePercent / 100m);

            weekStart = weekEnd.PlusDays(1);
        }
    }
}
