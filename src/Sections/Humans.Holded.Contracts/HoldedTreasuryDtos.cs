using NodaTime;

namespace Humans.Holded.Contracts;

/// <summary>One line of a treasury account's bank feed
/// (GET /api/v2/treasury/accounts/{id}/bank-movements). Shapes verified in the v2 probe notes,
/// src/Sections/Humans.Holded/Docs/2026-08-10-holded-v2-migration-design.md.</summary>
public sealed record HoldedBankMovementDto
{
    public required string Id { get; init; }
    /// <summary>`account` — the treasury account id the line belongs to.</summary>
    public required string AccountId { get; init; }
    /// <summary>Booking date, Europe/Madrid. A <c>LocalDate</c> rather than the <c>Instant</c> the
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

/// <summary>The <c>document_type</c> values reconcile accepts. <see cref="Purchase"/> is confirmed;
/// <see cref="LedgerEntry"/> is NOT — the probe could not confirm a daily-ledger entry is a valid
/// reconcile target, so callers must tolerate it being refused
/// (see the fallback ladder in the SEPA booking flow).</summary>
public static class HoldedReconcileDocumentType
{
    public const string Purchase = "purchase";
    public const string LedgerEntry = "dailyledger";
}
