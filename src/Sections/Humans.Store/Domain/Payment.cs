using NodaTime;

namespace Humans.Store.Domain;

internal sealed class Payment
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public decimal AmountEur { get; set; }
    public PaymentMethod Method { get; set; }

    /// <summary>
    /// String-stored twin of <see cref="Method"/>. Every insert writes both; older rows stay null until a Store admin
    /// fills them, and <see cref="Method"/> stays the one read until then.
    /// </summary>
    public PaymentMethod? MethodName { get; set; }

    /// <summary>
    /// <see cref="PaymentStatus.Paid"/> counts toward the order balance, and so does a <see cref="PaymentStatus.Pending"/>
    /// refund (negative amount): money on its way out is already owed to nobody.
    /// </summary>
    public PaymentStatus Status { get; set; } = PaymentStatus.Paid;

    /// <summary>The product whose deposit a <see cref="PaymentMethod.DepositReturn"/> returns: a kept deposit is income on
    /// that product's revenue account. Null on every other method, and on deposit returns recorded before it was
    /// asked for, which cover all of their order's deposits.</summary>
    public Guid? ProductId { get; set; }

    public string? StripePaymentIntentId { get; set; }
    public string? ExternalRef { get; set; }
    public Instant ReceivedAt { get; set; }
    public Guid? RecordedByUserId { get; set; }
    public string? Notes { get; set; }

    public Order? Order { get; set; }
}
