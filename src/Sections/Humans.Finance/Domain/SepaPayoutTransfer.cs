using NodaTime;

namespace Humans.Finance.Domain;

/// <summary>
/// One credit transfer inside a generated payout file. The row id tags every Holded posting the
/// booking makes, so a retry can see what was already posted.
/// </summary>
internal sealed class SepaPayoutTransfer
{
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

    /// <summary>Comma-joined Holded payment ids this transfer posted (<c>POST purchases/{id}/payments</c>),
    /// in posting order; an accepted-but-unreadable one is kept as its <c>unconfirmed:</c> sentinel.
    /// Written with the booking, and on a run that posted payments and then stopped short of
    /// booking, so a retry can refuse rather than post over them. Null on a row that never posted a
    /// payment. The sweep re-issues the bank-line reconcile from the confirmed ids while
    /// <see cref="ReconciledAt"/> is null.</summary>
    public string? HoldedPaymentRefs { get; set; }

    /// <summary>The Holded treasury bank-movement id this transfer was booked against — the Sabadell
    /// line that actually moved the money. Null on a row booked before nobodies-collective/Humans#1185.</summary>
    public string? HoldedBankMovementId { get; set; }

    /// <summary>When the bank line was reconciled against the postings in Holded. Set with a
    /// <see cref="BookedAt"/> and a <see cref="HoldedBankMovementId"/>, null means the reconcile is
    /// still pending and the sweep will retry it.</summary>
    public Instant? ReconciledAt { get; set; }

    /// <summary>When a finance admin withdrew this transfer because the bank cancelled it by hand
    /// before execution. A withdrawn row is kept for the record and the Article 15 export, but no
    /// matcher, booking or payability gate sees it again. Only an unbooked row can be withdrawn,
    /// so this and <see cref="BookedAt"/> are never both set.</summary>
    public Instant? WithdrawnAt { get; set; }

    /// <summary>The finance admin who withdrew it. Bare FK (no nav).</summary>
    public Guid? WithdrawnByUserId { get; set; }
}
