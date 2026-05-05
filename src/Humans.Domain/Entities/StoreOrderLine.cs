using NodaTime;

namespace Humans.Domain.Entities;

public class StoreOrderLine
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public Guid ProductId { get; set; }
    public int Qty { get; set; }
    public decimal UnitPriceSnapshot { get; set; }
    public decimal VatRateSnapshot { get; set; }
    public decimal? DepositAmountSnapshot { get; set; }
    public Instant AddedAt { get; set; }
    public Guid AddedByUserId { get; set; }

    public StoreOrder? Order { get; set; }
}
