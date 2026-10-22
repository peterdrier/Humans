using Humans.Store.Contracts;
using Humans.Store.Domain;
using NodaTime;

namespace Humans.Store.Data;

internal sealed record LineContext(
    Guid LineId,
    Guid OrderId,
    Guid? CampSeasonId,
    OrderState OrderState,
    LocalDate ProductOrderableUntil);

internal sealed record RecordedStripePayment(
    string PaymentIntentId,
    Guid OrderId,
    decimal AmountEur,
    Instant ReceivedAt,
    PaymentStatus Status);
