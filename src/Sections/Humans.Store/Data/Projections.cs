using Humans.Store.Contracts;
using Humans.Store.Domain;
using NodaTime;

namespace Humans.Store.Data;

/// <summary>
/// Materialized view of a <see cref="OrderLine"/> together with the parent
/// order's state and camp season and the product's order deadline. Returned by
/// <see cref="Repository.GetLineWithOrderAndProductAsync"/>.
/// </summary>
internal sealed record LineContext(
    Guid LineId,
    Guid OrderId,
    Guid? CampSeasonId,
    OrderState OrderState,
    LocalDate ProductOrderableUntil);

/// <summary>
/// A recorded Stripe-method <see cref="Payment"/> projected for reconciliation
/// against the Stripe Checkout Session list. Returned by
/// <see cref="Repository.GetRecordedStripePaymentsAsync"/>.
/// </summary>
internal sealed record RecordedStripePayment(
    string PaymentIntentId,
    Guid OrderId,
    decimal AmountEur,
    Instant ReceivedAt,
    PaymentStatus Status);
