using NodaTime;

namespace Humans.Domain.Entities;

public class StoreProduct
{
    public Guid Id { get; set; }
    public int Year { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public decimal UnitPriceEur { get; set; }
    public decimal VatRatePercent { get; set; }
    public decimal? DepositAmountEur { get; set; }
    public LocalDate OrderableUntil { get; set; }
    public bool IsActive { get; set; } = true;
    public Instant CreatedAt { get; set; }
    public Instant UpdatedAt { get; set; }
}
