namespace Humans.Monitor.Models;

/// <summary>
/// Page shell for the two Google-sync pages. The rows come from
/// <c>&lt;vc:google-sync-log&gt;</c>, which owns the read and the render; exactly one
/// of <see cref="ResourceId"/> / <see cref="UserId"/> is the predicate.
/// </summary>
internal sealed record SyncAuditViewModel(
    string Title,
    string? BackUrl,
    string? BackLabel,
    Guid? ResourceId,
    Guid? UserId);
