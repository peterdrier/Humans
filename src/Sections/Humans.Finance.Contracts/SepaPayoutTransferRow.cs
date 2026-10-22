using NodaTime;

namespace Humans.Finance.Contracts;

/// <summary>One credit transfer flattened with its file. The file's XML is left out: no consumer renders it.</summary>
/// <remarks>
/// <list type="bullet">
/// <item>The repository projects <paramref name="NotBookableReason"/>, <paramref name="CandidateBankMovementId"/> and
/// <paramref name="BatchLine"/> null; <c>GetSepaPayoutsAsync</c> fills them in.</item>
/// <item><paramref name="HoldedPaymentRefs"/> is comma-joined Holded payment ids, plus an <c>entry:&lt;amount&gt;</c>
/// token when a journal entry carried the remainder. On an unbooked row they are what a failed attempt landed; see
/// <see cref="PostedButNotBooked"/>.</item>
/// <item><paramref name="CandidateMatchedByText"/> is false when the line matched on amount and date alone because
/// Sabadell dropped the remittance text.</item>
/// <item><paramref name="WithdrawnAt"/> is set when the bank cancelled the transfer by hand before execution. A withdrawn
/// row is kept for the record only: nothing matches, books or blocks on it.</item>
/// </list>
/// </remarks>
public sealed record SepaPayoutTransferRow(
    Guid TransferId,
    Guid FileId,
    string FileName,
    Instant GeneratedAt,
    Guid GeneratedByUserId,
    Guid UserId,
    int SupplierAccountNum,
    string? HoldedContactId,
    string CreditorName,
    string IbanMasked,
    decimal Amount,
    Instant? BookedAt,
    Guid? BookedByUserId,
    string? HoldedBankMovementId,
    Instant? ReconciledAt,
    string? HoldedPaymentRefs,
    string? NotBookableReason,
    string? CandidateBankMovementId,
    LocalDate? CandidateBankMovementDate = null,
    decimal? CandidateBankMovementAmount = null,
    string? CandidateBankMovementDescription = null,
    SepaBatchLineVm? BatchLine = null,
    bool CandidateMatchedByText = false,
    Instant? WithdrawnAt = null,
    Guid? WithdrawnByUserId = null)
{
    /// <summary>There is no status column.</summary>
    public bool IsBooked => BookedAt is not null;

    /// <summary>Never true together with <see cref="IsBooked"/>: only an unbooked row can be withdrawn.</summary>
    public bool IsWithdrawn => WithdrawnAt is not null;

    /// <summary>Booked, against a known bank line, and Holded has not been told they match yet.</summary>
    public bool ReconcilePending =>
        IsBooked && HoldedBankMovementId is { Length: > 0 } && ReconciledAt is null;

    /// <summary>Records a journal-entry remainder in <see cref="HoldedPaymentRefs"/>, e.g. <c>entry:12.50</c>. Not a
    /// Holded id: never send it in a reconcile.</summary>
    public const string JournalEntryRefPrefix = "entry:";

    /// <summary>Null when none. Holded cannot link a ledger entry to a bank line, so that share is matched by hand in
    /// Holded and the row says so.</summary>
    public decimal? JournalEntryAmount
    {
        get
        {
            var token = (HoldedPaymentRefs ?? "").Split(',')
                .FirstOrDefault(r => r.StartsWith(JournalEntryRefPrefix, StringComparison.Ordinal));
            return token is not null
                   && decimal.TryParse(token[JournalEntryRefPrefix.Length..],
                       System.Globalization.NumberStyles.Number,
                       System.Globalization.CultureInfo.InvariantCulture, out var amount)
                ? amount
                : null;
        }
    }

    /// <summary>An attempt posted payments and stopped before booking. Holded drops the tag on a purchase payment, so a
    /// retry would post over them; the transfer is settled in Holded by hand instead.</summary>
    public bool PostedButNotBooked => !IsBooked && HoldedPaymentRefs is { Length: > 0 };

    /// <summary>The <c>CandidateBankMovement*</c> fields describe the found line so the treasurer can recognise it before
    /// clicking Book; they are filled together with the id.</summary>
    public bool CanBook =>
        !IsBooked && !IsWithdrawn && NotBookableReason is null && CandidateBankMovementId is { Length: > 0 };
}

/// <summary>A Sabadell line that looks like the bank debited a whole multi-transfer file at once. The sweep never books
/// it: a finance admin checks it and presses Process, which books every transfer in the file against it.</summary>
/// <remarks>
/// <paramref name="QuotesFileReference"/>: the line's text contains the file's <c>MsgId</c>/<c>PmtInfId</c>, rather than
/// matching on total and date alone. <paramref name="NotProcessableReason"/> is null when the file can be processed.
/// </remarks>
public sealed record SepaBatchLineVm(
    string MovementId,
    LocalDate Date,
    decimal Amount,
    string? Description,
    bool QuotesFileReference,
    string? NotProcessableReason);
