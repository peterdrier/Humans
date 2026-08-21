using Humans.AuditLog.Contracts;
using Humans.GoogleIntegration.Contracts;
using Humans.GoogleIntegration.Data;
using Humans.GoogleIntegration.Domain;

namespace Humans.GoogleIntegration.Services;

/// <summary>
/// <see cref="IGoogleSyncHistoryMigrationService"/> impl. Reads the legacy rows through
/// AuditLog's contract and writes through this section's own repository — neither section
/// touches the other's context.
/// </summary>
internal sealed class GoogleSyncHistoryMigrationService(
    ILegacyGoogleSyncAuditReader legacyAudit,
    IGoogleSyncLogRepository repo,
    ITeamResourceService teamResourceService,
    ILogger<GoogleSyncHistoryMigrationService> logger) : IGoogleSyncHistoryMigrationService
{
    /// <summary>Rows listed on the screen. The counts are always the whole set.</summary>
    private const int MaxDisplayRows = 200;

    public Task<GoogleSyncHistoryMigrationReport> PreviewAsync(CancellationToken ct = default) =>
        RunAsync(apply: false, ct);

    public Task<GoogleSyncHistoryMigrationReport> MigrateAsync(CancellationToken ct = default) =>
        RunAsync(apply: true, ct);

    private async Task<GoogleSyncHistoryMigrationReport> RunAsync(bool apply, CancellationToken ct)
    {
        var legacy = await legacyAudit.GetLegacyGoogleSyncRowsAsync(ct);
        if (legacy.Count == 0)
            return new GoogleSyncHistoryMigrationReport(0, 0, 0, 0, [], []);

        var alreadyPresent = await repo.GetExistingIdsAsync(legacy.Select(r => r.Id).ToList(), ct);

        var movable = new List<GoogleSyncLogEntry>();
        var skipped = new List<GoogleSyncHistorySkippedRow>();

        foreach (var row in legacy)
        {
            if (alreadyPresent.Contains(row.Id))
                continue;

            if (Map(row) is { } entry)
                movable.Add(entry);
            else
                skipped.Add(new GoogleSyncHistorySkippedRow(row.Id, row.OccurredAt, row.Action, SkipReason(row)));
        }

        var moved = 0;
        if (apply && movable.Count > 0)
        {
            await repo.AddRangeAsync(movable, ct);
            moved = movable.Count;
            logger.LogWarning(
                "Google sync history migration: copied {Moved} audit_log row(s) into google_sync_log " +
                "({AlreadyPresent} already present, {Skipped} unmappable)",
                moved, alreadyPresent.Count, skipped.Count);
        }

        return new GoogleSyncHistoryMigrationReport(
            Examined: legacy.Count,
            AlreadyPresent: alreadyPresent.Count,
            Movable: movable.Count,
            Moved: moved,
            MovableRows: await ToDisplayRowsAsync(movable, ct),
            SkippedRows: skipped.OrderByDescending(r => r.OccurredAt).Take(MaxDisplayRows).ToList());
    }

    /// <summary>
    /// The audit row keeps its id on the copy — that is what makes a second run a no-op.
    /// Returns null when the row carries no Google payload the sync log can hold.
    /// </summary>
    private static GoogleSyncLogEntry? Map(LegacyGoogleSyncAuditRow row)
    {
        var action = row.Action switch
        {
            AuditAction.GoogleResourceAccessGranted => GoogleSyncLogAction.AccessGranted,
            AuditAction.GoogleResourceAccessRevoked => GoogleSyncLogAction.AccessRevoked,
            _ => (GoogleSyncLogAction?)null
        };

        if (action is null || row.SyncSource is null || row.Success is null)
            return null;

        return new GoogleSyncLogEntry
        {
            Id = row.Id,
            Action = action.Value,
            ResourceId = row.ResourceId,
            UserId = string.Equals(row.RelatedEntityType, "User", StringComparison.Ordinal)
                ? row.RelatedEntityId
                : null,
            UserEmail = row.UserEmail ?? string.Empty,
            Role = row.Role ?? string.Empty,
            Source = row.SyncSource.Value,
            Success = row.Success.Value,
            ErrorMessage = row.ErrorMessage,
            // The writer stored "JobName: description"; keep the text verbatim and recover
            // the job name from its prefix.
            Description = row.Description,
            JobName = JobNameOf(row.Description),
            OccurredAt = row.OccurredAt
        };
    }

    private static string JobNameOf(string description)
    {
        var separator = description.IndexOf(": ", StringComparison.Ordinal);
        return separator > 0 ? description[..separator] : string.Empty;
    }

    private static string SkipReason(LegacyGoogleSyncAuditRow row) =>
        row.Action is not (AuditAction.GoogleResourceAccessGranted or AuditAction.GoogleResourceAccessRevoked)
            ? $"{row.Action} has no google_sync_log action"
            : "no sync payload — SyncSource or Success is null";

    private async Task<IReadOnlyList<GoogleSyncHistoryMovableRow>> ToDisplayRowsAsync(
        IReadOnlyList<GoogleSyncLogEntry> entries, CancellationToken ct)
    {
        if (entries.Count == 0)
            return [];

        var shown = entries.OrderByDescending(e => e.OccurredAt).Take(MaxDisplayRows).ToList();
        var names = await teamResourceService.GetResourceNamesByIdsAsync(
            shown.Select(e => e.ResourceId).Distinct().ToList(), ct);

        return shown
            .Select(e => new GoogleSyncHistoryMovableRow(
                e.Id,
                e.OccurredAt,
                e.Action,
                names.TryGetValue(e.ResourceId, out var name) ? name : null,
                e.UserEmail,
                e.Role,
                e.Source,
                e.Success))
            .ToList();
    }
}
