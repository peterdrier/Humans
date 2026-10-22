using NodaTime;

namespace Humans.Grants.Services;

internal enum GrantNoticeKind
{
    /// <summary>Also used for a resend.</summary>
    Issued,
    Revoked,
}

/// <summary>Pure data, so the builder stays I/O-free.</summary>
internal sealed record GrantNoticeRequest(
    string RecipientEmail,
    string? RecipientName,
    GrantNoticeKind Kind,
    string ProgramName,
    string Entitles,
    LocalDate? ExpiresOn,
    string Url,
    string? Reason = null,
    string? Culture = null);
