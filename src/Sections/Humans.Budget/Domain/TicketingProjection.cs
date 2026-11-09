using NodaTime;

namespace Humans.Budget.Domain;

/// <summary>One-to-one with a BudgetGroup whose IsTicketingGroup is true.</summary>
internal sealed class TicketingProjection
{
    public Guid Id { get; init; }
    public Guid BudgetGroupId { get; init; }
    public BudgetGroup? BudgetGroup { get; set; }

    public LocalDate? StartDate { get; set; }

    /// <summary>End of the sales period.</summary>
    public LocalDate? EventDate { get; set; }

    /// <summary>Pre-sale / first-day burst.</summary>
    public int InitialSalesCount { get; set; }

    /// <summary>Tickets per day after the initial burst.</summary>
    public decimal DailySalesRate { get; set; }

    /// <summary>Euros, gross (VAT-inclusive).</summary>
    public decimal AverageTicketPrice { get; set; }

    /// <summary>Percent; typically 10 in Spain.</summary>
    public int VatRate { get; set; }

    /// <summary>1.5 means 1.5%.</summary>
    public decimal StripeFeePercent { get; set; }

    /// <summary>Euros per transaction.</summary>
    public decimal StripeFeeFixed { get; set; }

    /// <summary>3.0 means 3%.</summary>
    public decimal TicketTailorFeePercent { get; set; }

    public Instant CreatedAt { get; init; }
    public Instant UpdatedAt { get; set; }

    // Previews and persisted lines both use this, so they share one sales and fee policy.
    internal IEnumerable<(LocalDate Start, LocalDate End, int Tickets, decimal Revenue,
        decimal StripeFees, decimal TicketTailorFees)> CalculateWeeks(LocalDate today)
    {
        if (StartDate is null || EventDate is null || StartDate.Value > EventDate.Value
            || AverageTicketPrice == 0 || today > EventDate.Value)
            yield break;

        var currentMonday = today.PlusDays(1 - (int)today.DayOfWeek);
        var weekStart = currentMonday > StartDate.Value
            ? currentMonday
            : StartDate.Value.PlusDays(1 - (int)StartDate.Value.DayOfWeek);
        var isFirstWeek = true;

        while (weekStart <= EventDate.Value)
        {
            var weekDays = Math.Min(7, Period.Between(weekStart, EventDate.Value, PeriodUnits.Days).Days + 1);
            var weekEnd = weekStart.PlusDays(weekDays - 1);
            var salesStart = weekStart < StartDate.Value ? StartDate.Value : weekStart;
            var salesDays = Period.Between(salesStart, weekEnd, PeriodUnits.Days).Days + 1;
            var tickets = (int)Math.Round(DailySalesRate * salesDays);
            if (isFirstWeek && weekStart <= StartDate.Value)
                tickets += InitialSalesCount;
            isFirstWeek = false;
            if (tickets <= 0) tickets = 1;

            var revenue = tickets * AverageTicketPrice;
            yield return (weekStart, weekEnd, tickets, revenue,
                revenue * StripeFeePercent / 100m + tickets * StripeFeeFixed,
                revenue * TicketTailorFeePercent / 100m);

            if (weekEnd == EventDate.Value) yield break;
            weekStart = weekEnd.PlusDays(1);
        }
    }
}
