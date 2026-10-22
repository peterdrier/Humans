using NodaTime;

namespace Humans.Holded.Contracts;

public sealed record HoldedBankMovementDto
{
    public required string Id { get; init; }
    /// <summary><c>booking_date</c>, falling back to <c>value_date</c>, never zone-shifted. A <c>LocalDate</c> because
    /// it feeds straight back into a posting date (<see cref="IHoldedClient.PayPurchaseDocumentAsync"/>), and a
    /// round trip through a midnight instant can only lose.</summary>
    public required LocalDate Date { get; init; }
    /// <summary>Signed euros: negative is money leaving the account.</summary>
    public required decimal Amount { get; init; }
    /// <summary>The bank's own text; a SEPA payout's remittance <c>&lt;account&gt; - NCA - &lt;name&gt;</c> reaches it.</summary>
    public string? Description { get; init; }
    /// <summary>`pending` | `partial` | `reconciled`, verbatim and lowercased.</summary>
    public required string Status { get; init; }
    public string? Origin { get; init; }
}

public sealed record HoldedReconcileDocumentRef(string DocumentId, string DocumentType);

/// <summary>Reconcile against the payment ids <c>POST purchases/{id}/payments</c> returned, never the purchase
/// documents: a <c>purchase</c> ref makes Holded mint a new payment for the whole line on the first document,
/// leaving the posted payments unmatched. A journal entry can't be named at all (<c>dailyledger</c>/<c>entry</c>
/// are accepted and do nothing), so a line partly paid to one stays <c>partial</c> until a human reconciles it.
/// Success answers <c>200 null</c>.</summary>
public static class HoldedReconcileDocumentType
{
    public const string Payment = "payment";
}
