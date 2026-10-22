using NodaTime;

namespace Humans.Finance.Domain;

/// <summary>The row id tags every Holded posting the booking makes, so a retry can see what was posted.</summary>
internal sealed class SepaPayoutTransfer
{
    public Guid Id { get; init; }

    public Guid FileId { get; init; }

    public Guid UserId { get; init; }

    /// <summary>The 400000xx/410000xx account the balance was read from.</summary>
    public int SupplierAccountNum { get; init; }

    /// <summary>Captured at generation because Holded lets two contacts share one account and only this says which the
    /// file named. Null on rows generated before it was captured.</summary>
    public string? HoldedContactId { get; init; }

    /// <summary>As written into <c>Cdtr/Nm</c>, already SEPA-normalized.</summary>
    public string CreditorName { get; init; } = "";

    /// <summary>Unmasked: this row and the XML are the only places it is kept.</summary>
    public string Iban { get; init; } = "";

    /// <summary>What every log, audit entry and screen shows.</summary>
    public string IbanMasked { get; init; } = "";

    public decimal Amount { get; init; }

    /// <summary>There is no status column: null is "not booked yet", and booking is refused while set.</summary>
    public Instant? BookedAt { get; set; }

    public Guid? BookedByUserId { get; set; }

    /// <summary>Comma-joined payment ids in posting order; an accepted-but-unreadable one is an <c>unconfirmed:</c>
    /// sentinel. Also written by a run that posted and stopped short of booking, so a retry refuses. The sweep
    /// re-issues the reconcile from these while <see cref="ReconciledAt"/> is null.</summary>
    public string? HoldedPaymentRefs { get; set; }

    /// <summary>The Sabadell line that moved the money. Null on rows booked before bank-line booking.</summary>
    public string? HoldedBankMovementId { get; set; }

    /// <summary>Null while booked means the reconcile is pending and the sweep retries it.</summary>
    public Instant? ReconciledAt { get; set; }

    /// <summary>The bank cancelled it by hand before execution. Kept for the record and the Article 15 export, but
    /// nothing matches, books or gates on it. Never set together with <see cref="BookedAt"/>.</summary>
    public Instant? WithdrawnAt { get; set; }

    public Guid? WithdrawnByUserId { get; set; }
}
