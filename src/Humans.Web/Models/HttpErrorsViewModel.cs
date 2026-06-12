namespace Humans.Web.Models;

/// <summary>
/// View model for the <c>/Debug/HttpErrors</c> screen: the rolling buffer of
/// recent error responses (newest first) plus lifetime per-code counts that
/// survive buffer eviction.
/// </summary>
public sealed record HttpErrorsViewModel(
    long TotalErrors,
    IReadOnlyList<HttpErrorCountRow> LifetimeCounts,
    IReadOnlyList<HttpErrorRow> Entries);

/// <summary>Lifetime count for one status code since process start.</summary>
public sealed record HttpErrorCountRow(int StatusCode, long Count);

/// <summary>One buffered error response. <paramref name="ClientLabel"/> is the
/// classified short form of <paramref name="UserAgent"/> (bot name, or browser · OS).
/// <paramref name="Timestamp"/> is UTC.</summary>
public sealed record HttpErrorRow(
    DateTime Timestamp,
    int StatusCode,
    string Method,
    string Url,
    string IpAddress,
    Guid? UserId,
    string ClientLabel,
    string UserAgent);
