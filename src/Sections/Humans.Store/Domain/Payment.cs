using NodaTime;

namespace Humans.Store.Domain;

internal sealed class Payment
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public decimal AmountEur { get; set; }
    public PaymentMethod Method { get; set; }

    /// <summary>
    /// String-stored twin of <see cref="Method"/>, replacing the int column. Every insert writes
    /// both; rows that predate this column are null until a Store admin fills them from
    /// <c>/Store/Admin/PaymentMethods</c>. <see cref="Method"/> stays the one read until then.
    /// </summary>
    public PaymentMethod? MethodName { get; set; }

    /// <summary>
    /// What Stripe has confirmed about this row's money. Sync payments (card/wallet) and manual
    /// entries are <see cref="PaymentStatus.Paid"/> at insert; async methods (SEPA, delayed
    /// Bizum) start <see cref="PaymentStatus.Pending"/> and transition to
    /// <see cref="PaymentStatus.Paid"/> or <see cref="PaymentStatus.Failed"/> when Stripe
    /// reports settlement. Only <see cref="PaymentStatus.Paid"/> counts toward the order balance.
    /// </summary>
    public PaymentStatus Status { get; set; } = PaymentStatus.Paid;

    public string? StripePaymentIntentId { get; set; }
    public string? ExternalRef { get; set; }
    public Instant ReceivedAt { get; set; }
    public Guid? RecordedByUserId { get; set; }
    public string? Notes { get; set; }

    public Order? Order { get; set; }
}
