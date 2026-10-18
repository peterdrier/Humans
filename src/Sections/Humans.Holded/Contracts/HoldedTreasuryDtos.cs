using NodaTime;

namespace Humans.Holded.Contracts;

/// <summary>One line of a treasury account's bank feed
/// (GET /api/v2/treasury/accounts/{id}/bank-movements). Shapes verified in the v2 probe notes,
/// src/Sections/Humans.Holded/Docs/2026-08-10-holded-v2-migration-design.md.</summary>
public sealed record HoldedBankMovementDto
{
    public required string Id { get; init; }
    /// <summary>Holded's <c>booking_date</c> (falling back to <c>value_date</c>), its date part as Holded sends it,
    /// never zone-shifted. A <c>LocalDate</c> rather than the <c>Instant</c> the
    /// ledger DTOs carry: this date is fed straight back into a posting date
    /// (<see cref="IHoldedClient.PayPurchaseDocumentAsync"/> takes a <c>LocalDate</c>), and a
    /// round-trip through a midnight instant can only lose in a zone conversion.</summary>
    public required LocalDate Date { get; init; }
    /// <summary>Signed euros: negative is money leaving the account.</summary>
    public required decimal Amount { get; init; }
    /// <summary>The bank's own text — for a SEPA payout the remittance
    /// <c>&lt;account&gt; - NCA - &lt;name&gt;</c> reaches it.</summary>
    public string? Description { get; init; }
    /// <summary>`pending` | `partial` | `reconciled`, verbatim and lowercased.</summary>
    public required string Status { get; init; }
    public string? Origin { get; init; }
}

/// <summary>One document a bank line is reconciled against.</summary>
public sealed record HoldedReconcileDocumentRef(string DocumentId, string DocumentType);

/// <summary>The one <c>document_type</c> a reconcile may carry. A bank line is reconciled against
/// the <b>payment</b> ids <c>POST purchases/{id}/payments</c> returned, never against the purchase
/// documents: a <c>purchase</c> ref reads as "this line pays that document", and Holded then mints
/// a brand-new payment for the whole line amount on the first document named, leaving the payments
/// already posted as unmatched legs (seen in production 2026-10-17). A journal entry cannot be named
/// at all — <c>dailyledger</c> / <c>entry</c> are accepted and silently do nothing — so a line whose
/// money partly went to an entry stays <c>partial</c> until a human reconciles it in Holded.
/// Verified live 2026-10-17: Holded returns <c>200 null</c>, the line reads <c>reconciled</c> with
/// <c>reconciled_amount</c> equal to the line amount, and every payment leg reads
/// <c>reconciliation_status: reconciled</c>.</summary>
public static class HoldedReconcileDocumentType
{
    public const string Payment = "payment";
}
