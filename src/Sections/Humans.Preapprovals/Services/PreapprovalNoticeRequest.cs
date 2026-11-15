using NodaTime;

namespace Humans.Preapprovals.Services;

/// <summary>Which email <see cref="PreapprovalsEmails"/> renders.</summary>
internal enum PreapprovalNoticeKind
{
    Issued,
    Declined,
    ApplicationReceived,
    DeadlineReminder,
    AmountChanged,
    Revoked,
    DeadlineChanged,
}

/// <summary>
/// Pure data, so the builder stays I/O-free. Amount changes carry the old amount as a number so the builder formats it
/// in the recipient's culture.
/// </summary>
internal sealed record PreapprovalNoticeRequest(
    Guid RecipientUserId,
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
