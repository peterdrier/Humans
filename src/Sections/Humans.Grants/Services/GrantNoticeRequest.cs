using NodaTime;

namespace Humans.Grants.Services;

/// <summary>Which member-facing email <see cref="GrantsEmails"/> renders.</summary>
internal enum GrantNoticeKind
{
    /// <summary>Issued, or resent: the program, what it entitles them to, its expiry.</summary>
    Issued,
    /// <summary>Revoked: the program, the reason if given.</summary>
    Revoked,
}

/// <summary>Everything one notice needs; pure data so the builder stays I/O-free.</summary>
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
