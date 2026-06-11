namespace Humans.Domain.Enums;

/// <summary>
/// What Stripe has confirmed about a <see cref="Entities.StorePayment"/>'s money — not what the
/// donor intended at checkout. A debit mandate is not a payment; the system must never imply
/// "paid" until Stripe confirms settlement. Balance computation counts <see cref="Paid"/> only.
/// </summary>
public enum StorePaymentStatus
{
    // Paid is the zero/default member on purpose: it is the value for every existing row (all
    // pre-date async support and are settled), the value for sync/manual inserts, and the column
    // default. Keeping it at 0 makes it EF's insert sentinel so an explicitly-set Pending (non-zero)
    // is never swallowed by the store default — the enum analogue of the bool-sentinel trap.

    /// <summary>Stripe confirmed settlement (sync at <c>completed</c>; async at <c>async_payment_succeeded</c>). Money is real; counted toward the order's paid total.</summary>
    Paid,

    /// <summary>Mandate captured, awaiting clearance (async methods: SEPA, delayed Bizum, iDEAL). No money has moved; excluded from the order's paid total.</summary>
    Pending,

    /// <summary>Mandate rejected or settlement bounced (<c>async_payment_failed</c>). Treated as zero; excluded from the order's paid total.</summary>
    Failed,
}
