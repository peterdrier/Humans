using Humans.Finance.Contracts;
using NodaTime;

namespace Humans.Finance.Models;

/// <summary>Every field is configuration-bound.</summary>
internal sealed record SepaDebtor(string Name, string Iban, string? Bic, string PresenterId);

/// <summary><paramref name="SupplierAccountNum"/> prefixes every id field and the remittance text, so a bank line ties
/// to a creditor account without opening the file.</summary>
internal sealed record SepaTransfer(string CreditorName, string Iban, decimal Amount, int SupplierAccountNum);

/// <summary>Pure data: the builder does no IO.</summary>
internal sealed record SepaPaymentFileRequest(
    string MsgId,
    Instant CreatedAt,
    LocalDate RequestedExecutionDate,
    SepaDebtor Debtor,
    decimal MaxAmountPerTransfer,
    IReadOnlyList<SepaTransfer> Transfers)
{
    /// <summary>For the SEPA creation timestamp.</summary>
    public DateTimeZone CreationTimeZone { get; init; } =
        DateTimeZoneProviders.Tzdb["Europe/Madrid"];
}

internal sealed record SepaPayoutSelection(int SupplierAccountNum, decimal Amount);

/// <summary>
/// All-or-nothing: one bad row refuses the whole generation, because a partly-sent batch is far harder to reconcile
/// than a re-run.
/// </summary>
internal sealed record SepaPayoutResult(string? FileName, string? Xml, string? ErrorMessage)
{
    public static SepaPayoutResult Failure(string message) => new(null, null, message);

    public bool Succeeded => ErrorMessage is null;
}

/// <summary>One transfer as the file would carry it. <paramref name="CreditorName"/> is the name the bank receives;
/// <paramref name="Owed"/> is the balance before this payment.</summary>
internal sealed record SepaPayoutPreviewLine(
    int SupplierAccountNum, Guid UserId, string CreditorName, string IbanMasked, decimal Owed, decimal Amount)
{
    public decimal OwedAfter => Owed - Amount;
}

internal sealed record SepaPayoutPreview(IReadOnlyList<SepaPayoutPreviewLine> Lines, string? ErrorMessage)
{
    public static SepaPayoutPreview Failure(string message) => new([], message);

    public bool Succeeded => ErrorMessage is null;
}

/// <summary><paramref name="Matches"/> is keyed by account and missing where no check applies.</summary>
internal sealed record SepaReviewVm(
    IReadOnlyList<SepaPayoutPreviewLine> Lines,
    IReadOnlyDictionary<int, CreditorMatchVm> Matches,
    decimal MaxPerTransfer)
{
    public decimal Total => Lines.Sum(l => l.Amount);
}

/// <summary><paramref name="UnavailableReason"/> is null when configured, otherwise what the admin must set.</summary>
internal sealed record SepaPayoutSettings(decimal MaxPerTransfer, string? UnavailableReason)
{
    public bool IsAvailable => UnavailableReason is null;
}

/// <summary>An outgoing Sabadell line the sweep could not book: the page's "needs a human" list.</summary>
internal sealed record SepaBankMovementVm(
    string MovementId,
    LocalDate Date,
    decimal Amount,
    string? Description,
    int? ParsedAccountNum,
    string Status,
    string Reason);

/// <summary>MemberName supplies native confirmation prompts; browser attribution renders Row's user IDs.</summary>
internal sealed record SepaTransferVm(SepaPayoutTransferRow Row, string MemberName);

/// <summary><paramref name="BatchLine"/> is the single bank line that debited the whole file, when there is
/// one.</summary>
internal sealed record SepaPayoutFileVm(
    Guid FileId,
    string FileName,
    Instant GeneratedAt,
    Guid GeneratedByUserId,
    IReadOnlyList<SepaTransferVm> Transfers,
    SepaBatchLineVm? BatchLine);

/// <summary>
/// <list type="bullet">
/// <item><paramref name="UnavailableReason"/> is set when missing configuration turns booking off for every row; the
/// page says so once.</item>
/// <item><paramref name="BankFeedError"/> is set when the live feed failed: every row falls back to "waiting for the
/// Sabadell line" and the page shows a banner instead of failing.</item>
/// <item><paramref name="ToleranceDays"/> is the page's editable matching window, posted back with every booking.</item>
/// </list>
/// </summary>
internal sealed record SepaPayoutsPageVm(
    IReadOnlyList<SepaPayoutFileVm> Files,
    string? UnavailableReason,
    IReadOnlyList<SepaBankMovementVm> UnmatchedMovements,
    string? BankFeedError,
    int ToleranceDays,
    SepaBookingRunVm? Run = null);

/// <summary>The current or last booking run started from the page. <paramref name="Result"/> is null while it runs.</summary>
internal sealed record SepaBookingRunVm(string What, Instant StartedAt, Instant? FinishedAt, SepaBookingResult? Result)
{
    public bool IsRunning => FinishedAt is null;
}

/// <summary><paramref name="Message"/> is admin-facing: the reason on failure, what was posted on success.</summary>
internal sealed record SepaBookingResult(bool Succeeded, string Message);

/// <summary>
/// For the member's Article 15 export. Masked IBAN only: the unmasked one stays in the payout row and the file.
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
    Instant? ReconciledAt,
    Instant? WithdrawnAt = null);
