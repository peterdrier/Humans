using Humans.Base.Enums;
using Humans.GoogleIntegration.Contracts;

namespace Humans.GoogleIntegration.Models;

/// <summary>Exactly one predicate is set; the view component does the read.</summary>
internal sealed record SyncAuditViewModel(
    string Title,
    string? BackUrl,
    string? BackLabel,
    Guid? ResourceId,
    Guid? UserId);

internal sealed record ActivityLogViewModel(
    NodaTime.LocalDate? From,
    NodaTime.LocalDate? To,
    GoogleSyncLogAction? Action,
    GoogleSyncSource? Source,
    bool? Success,
    string? Resource,
    IReadOnlyList<GoogleSyncLogView> Entries);
