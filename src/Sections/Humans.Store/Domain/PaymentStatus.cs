namespace Humans.Store.Domain;

/// <summary>
/// What Stripe has confirmed, not what the payer intended: a debit mandate is not a payment, so nothing reads as paid
/// until Stripe confirms settlement.
/// </summary>
internal enum PaymentStatus
{
    // Paid is the zero member on purpose: it is the default for sync and manual inserts.

    /// <summary>Settled: sync at <c>completed</c>, async at <c>async_payment_succeeded</c>.</summary>
    Paid,

    /// <summary>Mandate captured, awaiting clearance (SEPA, delayed Bizum, iDEAL). No money has moved.</summary>
    Pending,

    /// <summary>Mandate rejected or settlement bounced (<c>async_payment_failed</c>).</summary>
    Failed,
}
