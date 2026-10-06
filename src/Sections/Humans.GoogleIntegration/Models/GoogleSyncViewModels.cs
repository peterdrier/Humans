namespace Humans.GoogleIntegration.Models;

/// <summary>
/// Page shell for a resource or human's Google sync history. The view component owns
/// the sync-log read and render; exactly one predicate is populated.
/// </summary>
internal sealed record SyncAuditViewModel(
    string Title,
    string? BackUrl,
    string? BackLabel,
    Guid? ResourceId,
    Guid? UserId);
