using NodaTime;

namespace Humans.Finance.Models;

/// <summary>The organisation's side of the transfer — every field configuration-bound.</summary>
internal sealed record SepaDebtor(string Name, string Iban, string? Bic, string PresenterId);

/// <summary>One recipient. <paramref name="EndToEndId"/> comes from the persisted transfer row, and
/// <paramref name="SupplierAccountNum"/> is the 400000xx the remittance text is prefixed with so the
/// treasurer can tie a bank line to a creditor account without opening the file.</summary>
internal sealed record SepaTransfer(
    string EndToEndId, string CreditorName, string Iban, decimal Amount, int SupplierAccountNum);

/// <summary>Everything the file is built from. Pure data — the builder does no IO.</summary>
internal sealed record SepaPaymentFileRequest(
    string MsgId,
    string PmtInfId,
    Instant CreatedAt,
    LocalDate RequestedExecutionDate,
    SepaDebtor Debtor,
    decimal MaxAmountPerTransfer,
    IReadOnlyList<SepaTransfer> Transfers)
{
    /// <summary>The local zone used for the SEPA creation timestamp.</summary>
    public DateTimeZone CreationTimeZone { get; init; } =
        DateTimeZoneProviders.Tzdb["Europe/Madrid"];
}

/// <summary>One row of the payout the admin ticked, as posted from /Finance/Creditors.</summary>
internal sealed record SepaPayoutSelection(int SupplierAccountNum, decimal Amount);

/// <summary>
/// The generated file, or the reason there is none. All-or-nothing: one bad row refuses the whole
/// generation, because a partially-sent batch is far harder to reconcile than a re-run.
/// </summary>
internal sealed record SepaPayoutResult(string? FileName, string? Xml, string? ErrorMessage)
{
    public static SepaPayoutResult Failure(string message) => new(null, null, message);

    public bool Succeeded => ErrorMessage is null;
}

/// <summary>Whether SEPA generation is available at all, and the ceiling it enforces.</summary>
/// <param name="UnavailableReason">Null when configured; otherwise what the admin must set.</param>
internal sealed record SepaPayoutSettings(decimal MaxPerTransfer, string? UnavailableReason)
{
    public bool IsAvailable => UnavailableReason is null;
}

/// <summary>
/// One credit transfer flattened with the file it belongs to, for <c>/Finance/Sepa</c>. The file's
/// XML is deliberately absent — the screen lists hundreds of rows and never renders the document.
/// </summary>
/// <param name="NotBookableReason">Why this transfer cannot be booked into Holded, or null when it
/// can. The repository projects it null; <c>GetSepaPayoutsAsync</c> fills it in.</param>
/// <param name="CandidateBankMovementId">The Sabadell line that matches this transfer, filled in by
/// <c>GetSepaPayoutsAsync</c>; the repository always projects it null.</param>
internal sealed record SepaPayoutTransferRow(
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
    string? CandidateBankMovementDescription = null)
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

/// <summary>An outgoing Sabadell line the sweep could not book, with why — the page's
/// "a human has to look at this" list (nobodies-collective/Humans#1185).</summary>
internal sealed record SepaBankMovementVm(
    string MovementId,
    LocalDate Date,
    decimal Amount,
    string? Description,
    int? ParsedAccountNum,
    string Status,
    string Reason);

/// <summary>One transfer on <c>/Finance/Sepa</c>, with the two user ids on it resolved to names.</summary>
internal sealed record SepaTransferVm(SepaPayoutTransferRow Row, string MemberName, string? BookedByName);

/// <summary>One generated file and its transfers, as the screen groups them.</summary>
internal sealed record SepaPayoutFileVm(
    string FileName,
    Instant GeneratedAt,
    string GeneratedByName,
    IReadOnlyList<SepaTransferVm> Transfers);

/// <summary>The /Finance/Sepa page model.</summary>
/// <param name="UnavailableReason">Set when booking is off for every row (missing configuration);
/// the page says so once instead of repeating it on each row.</param>
/// <param name="UnmatchedMovements">Bank lines the live feed returned that matched no unbooked
/// transfer, or matched ambiguously — the "needs a human" panel.</param>
/// <param name="BankFeedError">Set when the live bank-feed call failed; every row then falls back to
/// "waiting for the Sabadell line" and the page renders a warning banner instead of failing.</param>
internal sealed record SepaPayoutsPageVm(
    IReadOnlyList<SepaPayoutFileVm> Files,
    string? UnavailableReason,
    IReadOnlyList<SepaBankMovementVm> UnmatchedMovements,
    string? BankFeedError);

/// <summary>The outcome of one booking attempt. <paramref name="Message"/> is admin-facing either
/// way — on failure it is the reason, on success what was posted.</summary>
internal sealed record SepaBookingResult(bool Succeeded, string Message);

/// <summary>
/// One credit transfer made to the member, flattened with the file it belongs to, for their GDPR
/// Article 15 export. Carries the masked IBAN only — the unmasked one stays in the payout row and
/// the file, which is the whole point of storing both.
/// </summary>
internal sealed record SepaPayoutExportRow(
    Instant GeneratedAt,
    string FileName,
    int SupplierAccountNum,
    string? HoldedContactId,
    string CreditorName,
    string IbanMasked,
    decimal Amount,
    Instant? BookedAt,
    string? HoldedBankMovementId,
    Instant? ReconciledAt);
