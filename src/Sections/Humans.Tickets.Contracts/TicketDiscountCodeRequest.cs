using NodaTime;

namespace Humans.Tickets.Contracts;

/// <summary>
/// What a section asks Tickets for when it needs discount codes: how many, what
/// kind of discount, how much, and when they stop working.
/// </summary>
/// <remarks>
/// This is deliberately <em>not</em> the vendor port's <c>DiscountCodeSpec</c>, and the
/// two drifting apart is the point (Finance's <c>CreditorLedgerLine</c> shape). The
/// port's spec is shaped by what a ticket vendor will accept; this one is shaped by
/// what the application means. Tickets maps between them at the edge — the only place
/// in the codebase that names both vocabularies — so when the 2027 vendor's notion of a
/// discount is not percentage-or-fixed, <see cref="TicketDiscountKind"/> absorbs it and
/// no consumer is recompiled.
/// </remarks>
public sealed record TicketDiscountCodeRequest(
    int Count,
    TicketDiscountKind Kind,
    decimal Value,
    Instant? ExpiresAt);

/// <summary>How a <see cref="TicketDiscountCodeRequest"/> reduces the ticket price.</summary>
public enum TicketDiscountKind
{
    /// <summary>A percentage off the ticket price.</summary>
    Percentage,

    /// <summary>A fixed amount off the ticket price, in the event's currency.</summary>
    Fixed,
}
