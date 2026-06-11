using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Domain.Entities;

public class StorePayment
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public decimal AmountEur { get; set; }
    public StorePaymentMethod Method { get; set; }

    /// <summary>
    /// What Stripe has confirmed about this row's money. Sync payments (card/wallet) and manual
    /// entries are <see cref="StorePaymentStatus.Paid"/> at insert; async methods (SEPA, delayed
    /// Bizum) start <see cref="StorePaymentStatus.Pending"/> and transition to
    /// <see cref="StorePaymentStatus.Paid"/> or <see cref="StorePaymentStatus.Failed"/> when Stripe
    /// reports settlement. Only <see cref="StorePaymentStatus.Paid"/> counts toward the order balance.
    /// </summary>
    public StorePaymentStatus Status { get; set; } = StorePaymentStatus.Paid;

    public string? StripePaymentIntentId { get; set; }
    public string? ExternalRef { get; set; }
    public Instant ReceivedAt { get; set; }
    public Guid? RecordedByUserId { get; set; }
    public string? Notes { get; set; }

    public StoreOrder? Order { get; set; }
}
