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

    /// <summary>Only <see cref="PaymentStatus.Paid"/> counts toward the order balance.</summary>
    public PaymentStatus Status { get; set; } = PaymentStatus.Paid;

    public string? StripePaymentIntentId { get; set; }
    public string? ExternalRef { get; set; }
    public Instant ReceivedAt { get; set; }
    public Guid? RecordedByUserId { get; set; }
    public string? Notes { get; set; }

    public Order? Order { get; set; }
}
