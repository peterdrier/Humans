using NodaTime;

namespace Humans.Tickets.Contracts;

/// <summary>
/// Deliberately not the vendor port's <c>DiscountCodeSpec</c>: this is shaped by what the application means, that
/// by what a vendor accepts. Tickets maps between them, so a new vendor's discount model changes no consumer.
/// </summary>
public sealed record TicketDiscountCodeRequest(
    int Count,
    TicketDiscountKind Kind,
    decimal Value,
    Instant? ExpiresAt);

public enum TicketDiscountKind
{
    Percentage,

    /// <summary>In the event's currency.</summary>
    Fixed,
}
