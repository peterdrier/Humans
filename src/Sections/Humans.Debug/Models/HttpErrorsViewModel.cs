namespace Humans.Debug.Models;

/// <summary>Per-code counts are lifetime totals that survive buffer eviction.</summary>
internal sealed record HttpErrorsViewModel(
    long TotalErrors,
    IReadOnlyList<HttpErrorCountRow> LifetimeCounts,
    IReadOnlyList<HttpErrorRow> Entries);

internal sealed record HttpErrorCountRow(int StatusCode, long Count);

/// <summary>One buffered error response. <paramref name="ClientLabel"/> is the
/// classified short form of <paramref name="UserAgent"/> (bot name, or browser · OS).
/// <paramref name="Timestamp"/> is UTC.</summary>
internal sealed record HttpErrorRow(
    DateTime Timestamp,
    int StatusCode,
    string Method,
    string Url,
    string IpAddress,
    Guid? UserId,
    string ClientLabel,
    string UserAgent);
