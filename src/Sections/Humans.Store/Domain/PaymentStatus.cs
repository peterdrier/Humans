namespace Humans.Store.Domain;

/// <summary>
/// What Stripe has confirmed about a <see cref="Payment"/>'s money — not what the
/// donor intended at checkout. A debit mandate is not a payment; the system must never imply
/// "paid" until Stripe confirms settlement. Balance computation counts <see cref="Paid"/> only.
/// </summary>
internal enum PaymentStatus
{
    // Paid is the zero/default member on purpose: it is the value for every existing row (all
    // pre-date async support and are settled) and the value sync and manual inserts want. The
    // column carried a matching default only for the AddStorePaymentStatus migration and no
    // longer does; Payment.Status's C# initializer is what covers inserts today.

    /// <summary>Stripe confirmed settlement (sync at <c>completed</c>; async at <c>async_payment_succeeded</c>). Money is real; counted toward the order's paid total.</summary>
    Paid,

    /// <summary>Mandate captured, awaiting clearance (async methods: SEPA, delayed Bizum, iDEAL). No money has moved; excluded from the order's paid total.</summary>
    Pending,

    /// <summary>Mandate rejected or settlement bounced (<c>async_payment_failed</c>). Treated as zero; excluded from the order's paid total.</summary>
    Failed,
}
