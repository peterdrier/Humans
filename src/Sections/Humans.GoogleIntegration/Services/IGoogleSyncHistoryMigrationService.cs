using Humans.Base.Enums;
using Humans.Base.Interfaces;
using Humans.GoogleIntegration.Contracts;
using NodaTime;

namespace Humans.GoogleIntegration.Services;

/// <summary>
/// One-time copy of Google-sync history from <c>audit_log</c> into <c>google_sync_log</c>; source rows are never
/// touched. Idempotent: a copy keeps the audit row's id. Goes when the six audit columns do.
/// </summary>
internal interface IGoogleSyncHistoryMigrationService : IApplicationService
{
    /// <summary>What a run would do. Writes nothing.</summary>
    Task<GoogleSyncHistoryMigrationReport> PreviewAsync(CancellationToken ct = default);

    Task<GoogleSyncHistoryMigrationReport> MigrateAsync(CancellationToken ct = default);
}

/// <summary>Counts for one preview or run, plus the rows behind them.</summary>
/// <param name="Examined">Audit rows carrying a Google resource id.</param>
/// <param name="AlreadyPresent">Of those, ones already in <c>google_sync_log</c>.</param>
/// <param name="Movable">Of the rest, ones that map to a sync-log row.</param>
/// <param name="Moved">Rows actually written — always 0 for a preview.</param>
/// <param name="MovableRows">The movable rows, newest first, capped for display.</param>
/// <param name="SkippedRows">Rows that map to nothing, newest first, capped for display.</param>
internal sealed record GoogleSyncHistoryMigrationReport(
    int Examined,
    int AlreadyPresent,
    int Movable,
    int Moved,
    IReadOnlyList<GoogleSyncHistoryMovableRow> MovableRows,
    IReadOnlyList<GoogleSyncHistorySkippedRow> SkippedRows)
{
    /// <summary>Rows that map to nothing and stay behind.</summary>
    public int Skipped => Examined - AlreadyPresent - Movable;
}

/// <summary>An audit row as it will land in <c>google_sync_log</c>.</summary>
internal sealed record GoogleSyncHistoryMovableRow(
    Guid AuditId,
    Instant OccurredAt,
    GoogleSyncLogAction Action,
    string? ResourceName,
    string UserEmail,
    string Role,
    GoogleSyncSource Source,
    bool Success);

/// <summary>An audit row the migration cannot map, and why.</summary>
internal sealed record GoogleSyncHistorySkippedRow(
    Guid AuditId,
    Instant OccurredAt,
    string Action,
    string Reason);
