using Humans.Base.Enums;
using Humans.GoogleIntegration.Contracts;

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

/// <summary>The Activity Log page: the submitted filters plus the matching sync-log rows.</summary>
internal sealed record ActivityLogViewModel(
    NodaTime.LocalDate? From,
    NodaTime.LocalDate? To,
    GoogleSyncLogAction? Action,
    GoogleSyncSource? Source,
    bool? Success,
    string? Resource,
    IReadOnlyList<GoogleSyncLogView> Entries);
