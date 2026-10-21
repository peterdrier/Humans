namespace Humans.Store.Domain;

/// <summary>
/// What Stripe has confirmed about a <see cref="Payment"/>'s money — not what the
/// donor intended at checkout. A debit mandate is not a payment; the system must never imply
/// "paid" until Stripe confirms settlement. Balance computation counts <see cref="Paid"/> only.
/// </summary>
internal enum PaymentStatus
{
    // Paid is the zero member on purpose: it is the default for sync and manual inserts.

    /// <summary>Stripe confirmed settlement (sync at <c>completed</c>; async at <c>async_payment_succeeded</c>). Money is real; counted toward the order's paid total.</summary>
    Paid,

    /// <summary>Mandate captured, awaiting clearance (async methods: SEPA, delayed Bizum, iDEAL). No money has moved; excluded from the order's paid total.</summary>
    Pending,

    /// <summary>Mandate rejected or settlement bounced (<c>async_payment_failed</c>). Treated as zero; excluded from the order's paid total.</summary>
    Failed,
}
