using NodaTime;

namespace Humans.Preapprovals.Services;

/// <summary>Which member-facing email <see cref="PreapprovalsEmails"/> renders.</summary>
internal enum PreapprovalNoticeKind
{
    /// <summary>Grant issued or application approved: program, amount, claim by, link to file.</summary>
    Issued,
    /// <summary>Application declined: program, the manager's note if any.</summary>
    Declined,
    /// <summary>To each manager: who applied, what they asked for, link to the manage page.</summary>
    ApplicationReceived,
    /// <summary>The nightly 14-day and 3-day reminder: file by {date}.</summary>
    DeadlineReminder,
    /// <summary>Program, old and new amount.</summary>
    AmountChanged,
    /// <summary>Program, label, amount, reason if given; what to do when a report holds it.</summary>
    Revoked,
    /// <summary>The claim deadline moved: the new date.</summary>
    DeadlineChanged,
}

/// <summary>
/// Everything one notice needs; pure data so the builder stays I/O-free. <c>Detail</c> is the
/// free text of the kind: the manager's note or the revocation reason. Amount changes
/// carry the old amount as a number so the builder formats it in the recipient's culture.
/// </summary>
internal sealed record PreapprovalNoticeRequest(
    string RecipientEmail,
    string? RecipientName,
    PreapprovalNoticeKind Kind,
    string ProgramName,
    string? Label,
    decimal Amount,
    LocalDate ClaimBy,
    string Url,
    string? Detail = null,
    string? Culture = null,
    decimal? PreviousAmount = null);
