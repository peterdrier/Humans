using NodaTime;

namespace Humans.Finance.Domain;

/// <summary>
/// One credit transfer inside a generated payout file. The row id is what the file's
/// <c>EndToEndId</c> is derived from, so it is the handle the bank and Holded share.
/// </summary>
internal sealed class SepaPayoutTransfer
{
    /// <summary>Also the source of this transfer's <c>EndToEndId</c>; never changes.</summary>
    public Guid Id { get; init; }

    public Guid FileId { get; init; }

    /// <summary>The member paid. Bare FK (no nav).</summary>
    public Guid UserId { get; init; }

    /// <summary>The 400000xx/410000xx creditor account the balance was read from.</summary>
    public int SupplierAccountNum { get; init; }

    /// <summary>The Holded contact whose recipient details this transfer actually paid — captured
    /// at generation, alongside <see cref="SupplierAccountNum"/>, because Holded lets two contacts
    /// share one account and only this id says which of them the file named. Null on a row
    /// generated before nobodies-collective/Humans#1146 shipped.</summary>
    public string? HoldedContactId { get; init; }

    /// <summary>Legal name as written into <c>Cdtr/Nm</c>, already SEPA-normalized.</summary>
    public string CreditorName { get; init; } = "";

    /// <summary>Unmasked — this row and the XML are the only places it is kept.</summary>
    public string Iban { get; init; } = "";

    /// <summary>What every log, audit entry and screen shows instead of <see cref="Iban"/>.</summary>
    public string IbanMasked { get; init; } = "";

    public decimal Amount { get; init; }

    /// <summary>When the payment was booked into Holded. Null is the whole "not booked yet" state —
    /// there is no status column, and booking is refused while this is set.</summary>
    public Instant? BookedAt { get; set; }

    /// <summary>The finance admin who pressed Book. Bare FK (no nav).</summary>
    public Guid? BookedByUserId { get; set; }

    /// <summary>Comma-joined Holded payment ids from before nobodies-collective/Humans#1185 moved
    /// the record onto <see cref="HoldedBankMovementId"/>. Retained unused so no shipped row loses
    /// what it holds; dropping the column is its own PR and needs Peter's approval
    /// (memory/architecture/no-drops-until-prod-verified.md). Nothing reads or writes it.</summary>
    public string? HoldedPaymentRefs { get; set; }

    /// <summary>The Holded treasury bank-movement id this transfer was booked against — the Sabadell
    /// line that actually moved the money. Null on a row booked before nobodies-collective/Humans#1185.</summary>
    public string? HoldedBankMovementId { get; set; }

    /// <summary>When the bank line was reconciled against the postings in Holded. Set with a
    /// <see cref="BookedAt"/> and a <see cref="HoldedBankMovementId"/>, null means the reconcile is
    /// still pending and the sweep will retry it.</summary>
    public Instant? ReconciledAt { get; set; }
}
