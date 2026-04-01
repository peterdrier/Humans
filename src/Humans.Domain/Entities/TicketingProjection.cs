using NodaTime;

namespace Humans.Domain.Entities;

/// <summary>
/// Projection parameters for a ticketing budget group.
/// Configures revenue, fee, and VAT projections for ticket sales.
/// One-to-one with BudgetGroup where IsTicketingGroup = true.
/// </summary>
public class TicketingProjection
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
}
