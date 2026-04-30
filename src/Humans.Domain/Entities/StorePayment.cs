using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Domain.Entities;

public class StorePayment
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public decimal AmountEur { get; set; }
    public StorePaymentMethod Method { get; set; }
    public string? StripePaymentIntentId { get; set; }
    public string? ExternalRef { get; set; }
    public Instant ReceivedAt { get; set; }
    public Guid? RecordedByUserId { get; set; }
    public string? Notes { get; set; }

    public StoreOrder? Order { get; set; }
}
