using NodaTime;

namespace Humans.Finance.Models;

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
internal sealed record SepaPayoutTransferRow(
    Guid TransferId,
    Guid FileId,
    string FileName,
    Instant GeneratedAt,
    Guid GeneratedByUserId,
    Guid UserId,
    int SupplierAccountNum,
    string CreditorName,
    string IbanMasked,
    decimal Amount,
    Instant? BookedAt,
    Guid? BookedByUserId,
    string? HoldedPaymentRefs,
    string? NotBookableReason)
{
    /// <summary>Booked is exactly "has a <see cref="BookedAt"/>" — there is no status column.</summary>
    public bool IsBooked => BookedAt is not null;

    public bool CanBook => !IsBooked && NotBookableReason is null;
}

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
internal sealed record SepaPayoutsPageVm(
    IReadOnlyList<SepaPayoutFileVm> Files,
    string? UnavailableReason);

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
    string CreditorName,
    string IbanMasked,
    decimal Amount,
    Instant? BookedAt);
