using NodaTime;

namespace Humans.Finance.Contracts;

/// <summary>
/// One credit transfer flattened with the file it belongs to, for <c>/Finance/Sepa</c> and
/// Backdoor's read-only <c>sepa-transfers</c> route (peterdrier/Humans#1838). The file's XML is
/// deliberately absent — both consumers list rows and never render the document.
/// </summary>
/// <param name="NotBookableReason">Why this transfer cannot be booked into Holded, or null when it
/// can. The repository projects it null; <c>GetSepaPayoutsAsync</c> fills it in.</param>
/// <param name="CandidateBankMovementId">The Sabadell line that matches this transfer, filled in by
/// <c>GetSepaPayoutsAsync</c>; the repository always projects it null.</param>
/// <param name="TransferId">The transfer's identifier.</param>
/// <param name="FileId">The identifier of the SEPA file containing this transfer.</param>
/// <param name="FileName">The name of the SEPA file containing this transfer.</param>
/// <param name="GeneratedAt">When the SEPA file was generated.</param>
/// <param name="GeneratedByUserId">The user who generated the SEPA file.</param>
/// <param name="UserId">The member receiving this transfer.</param>
/// <param name="SupplierAccountNum">The creditor's Holded supplier account number.</param>
/// <param name="HoldedContactId">The creditor's Holded contact identifier, if linked.</param>
/// <param name="CreditorName">The creditor name recorded for the transfer.</param>
/// <param name="IbanMasked">The creditor's masked IBAN.</param>
/// <param name="Amount">The transfer amount.</param>
/// <param name="BookedAt">When the transfer was booked, or null if it has not been booked.</param>
/// <param name="BookedByUserId">The user who booked the transfer, if booked.</param>
/// <param name="HoldedBankMovementId">The matching Holded bank movement identifier, if reconciled.</param>
/// <param name="ReconciledAt">When the transfer was reconciled with Holded, if reconciled.</param>
/// <param name="CandidateBankMovementDate">The candidate bank movement's date, when matched.</param>
/// <param name="CandidateBankMovementAmount">The candidate bank movement's amount, when matched.</param>
/// <param name="CandidateBankMovementDescription">The candidate bank movement's description, when matched.</param>
/// <param name="BatchLine">The candidate whole-file bank movement, when one is identified.</param>
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
    string? NotBookableReason,
    string? CandidateBankMovementId,
    LocalDate? CandidateBankMovementDate = null,
    decimal? CandidateBankMovementAmount = null,
    string? CandidateBankMovementDescription = null,
    SepaBatchLineVm? BatchLine = null)
{
    /// <summary>Booked is exactly "has a <see cref="BookedAt"/>" — there is no status column.</summary>
    public bool IsBooked => BookedAt is not null;

    /// <summary>Booked, against a known bank line, and Holded has not been told they match yet.</summary>
    public bool ReconcilePending =>
        IsBooked && HoldedBankMovementId is { Length: > 0 } && ReconciledAt is null;

    /// <summary>A bank line was found for this transfer and everything else checks out. The three
    /// <c>CandidateBankMovement*</c> fields describe that line, so the treasurer can recognise it
    /// before clicking Book; they are filled together with the id and are null without it.</summary>
    public bool CanBook =>
        !IsBooked && NotBookableReason is null && CandidateBankMovementId is { Length: > 0 };
}

/// <summary>One Sabadell line that looks like the bank debited a whole multi-transfer file at once —
/// its amount is the file's total. Never booked by the sweep: a finance admin checks it and presses
/// Process, which books every transfer in the file against it. Filled by <c>GetSepaPayoutsAsync</c>
/// onto every row of that file; the repository always projects it null.</summary>
/// <param name="QuotesFileReference">The line's text contains the file's id — the
/// <c>MsgId</c>/<c>PmtInfId</c> the bank was sent — rather than matching on total and date alone.</param>
/// <param name="NotProcessableReason">Why the file cannot be processed as it stands (a transfer in it
/// cannot be booked), or null when it can.</param>
/// <param name="MovementId">The bank movement identifier.</param>
/// <param name="Date">The date of the bank movement.</param>
/// <param name="Amount">The amount debited for the file.</param>
/// <param name="Description">The bank movement description, if available.</param>
public sealed record SepaBatchLineVm(
    string MovementId,
    LocalDate Date,
    decimal Amount,
    string? Description,
    bool QuotesFileReference,
    string? NotProcessableReason);
