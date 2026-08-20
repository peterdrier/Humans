using Humans.Base.Attributes;
using Humans.Base.Extensions;
using Humans.AuditLog.Contracts;
using Humans.Gdpr.Contracts;
using Humans.AuditLog.Data;
using Humans.AuditLog.Domain;
using Humans.Base.Enums;
using NodaTime;
using Humans.Users.Contracts;

namespace Humans.AuditLog.Services;

/// <summary>
/// <see cref="IAuditLogService"/> impl. Append-only (design-rules §12); best-effort — repo failures logged and swallowed (§7a).
/// Callers must audit AFTER business save. Also <see cref="IUserDataContributor"/> for GDPR export.
/// </summary>
[DontFix(
    reason: "Audit (crosscut) reads merged-account source IDs via IUserServiceRead so queries follow merged identities. Extracting merge-id resolution to a standalone leaf is deferred and Peter-owned.",
    since: "2026-05-25")]
internal sealed class AuditLogService(
    IAuditLogRepository repo,
    IUserServiceRead userService,
    IClock clock,
    ILogger<AuditLogService> logger)
    : IAuditLogService, IAuditLogReader, IUserDataContributor, ILegacyGoogleSyncAuditReader
{
    // ─── Writes (append-only) ───

    /// <inheritdoc />
    public async Task LogAsync(AuditAction action, string entityType, Guid entityId,
        string description, string jobName,
        Guid? relatedEntityId = null, string? relatedEntityType = null)
    {
        var entry = new AuditLogEntry
        {
            Id = Guid.NewGuid(),
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            Description = $"{jobName}: {description}",
            OccurredAt = clock.GetCurrentInstant(),
            ActorUserId = null,
            RelatedEntityId = relatedEntityId,
            RelatedEntityType = relatedEntityType
        };

        await PersistAsync(entry);

        logger.LogInformation("Audit: {Action} on {EntityType} {EntityId} by {Actor} — {Description}",
            action, entityType, entityId, jobName, description);
    }

    /// <inheritdoc />
    public async Task LogAsync(AuditAction action, string entityType, Guid entityId,
        string description, Guid actorUserId,
        Guid? relatedEntityId = null, string? relatedEntityType = null)
    {
        var entry = new AuditLogEntry
        {
            Id = Guid.NewGuid(),
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            Description = description,
            OccurredAt = clock.GetCurrentInstant(),
            ActorUserId = actorUserId,
            RelatedEntityId = relatedEntityId,
            RelatedEntityType = relatedEntityType
        };

        await PersistAsync(entry);

        logger.LogInformation("Audit: {Action} on {EntityType} {EntityId} by user {ActorUserId} — {Description}",
            action, entityType, entityId, actorUserId, description);
    }

    private async Task PersistAsync(AuditLogEntry entry)
    {
        try
        {
            await repo.AddAsync(entry);
        }
        catch (Exception ex)
        {
            // Best-effort: log loudly, do not propagate (would break the business op).
            logger.LogError(ex,
                "Failed to persist audit entry {EntryId} ({Action} on {EntityType} {EntityId})",
                entry.Id, entry.Action, entry.EntityType, entry.EntityId);
        }
    }

    // ─── Reads ───

    /// <inheritdoc />
    public async Task<IReadOnlyList<AuditLogEntrySnapshot>> GetRecentAsync(int count, CancellationToken ct = default)
    {
        var entries = await repo.GetRecentAsync(count, ct);
        return entries.Select(ToSnapshot).ToList();
    }

    private static AuditLogEntrySnapshot ToSnapshot(AuditLogEntry entry) =>
        new(
            entry.Id,
            entry.Action,
            entry.EntityType,
            entry.EntityId,
            entry.Description,
            entry.OccurredAt,
            entry.ActorUserId,
            entry.RelatedEntityId,
            entry.RelatedEntityType);

    /// <inheritdoc />
    public async Task<(IReadOnlyList<AuditLogEntrySnapshot> Items, int TotalCount, int AnomalyCount)> GetFilteredAsync(
        string? actionFilter, int page, int pageSize, CancellationToken ct = default)
    {
        AuditAction? parsed = null;
        if (!string.IsNullOrWhiteSpace(actionFilter) &&
            Enum.TryParse<AuditAction>(actionFilter, ignoreCase: true, out var action))
        {
            parsed = action;
        }

        var (items, totalCount, anomalyCount) = await repo.GetFilteredAsync(parsed, page, pageSize, ct);
        return (items.Select(ToSnapshot).ToList(), totalCount, anomalyCount);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AuditLogEntrySnapshot>> GetByUserAsync(Guid userId, int count, CancellationToken ct = default)
    {
        // Chain-follow merge tombstones for source-id-attributed rows.
        var sourceIds = await userService.GetMergedSourceIdsAsync(userId, ct);
        if (sourceIds.Count == 0)
        {
            var entries = await repo.GetByUserAsync(userId, count, ct);
            return entries.Select(ToSnapshot).ToList();
        }

        var allIds = new List<Guid>(sourceIds.Count + 1);
        allIds.AddRange(sourceIds);
        allIds.Add(userId);
        var mergedEntries = await repo.GetByUserIdsAsync(allIds, count, ct);
        return mergedEntries.Select(ToSnapshot).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AuditLogEntrySnapshot>> GetFilteredEntriesAsync(
        string? entityType = null,
        Guid? entityId = null,
        Guid? userId = null,
        IReadOnlyList<AuditAction>? actions = null,
        int limit = 20,
        CancellationToken ct = default)
    {
        // Chain-follow merge tombstones when userId is supplied.
        IReadOnlyCollection<Guid>? userIds = null;
        if (userId.HasValue)
        {
            var sourceIds = await userService.GetMergedSourceIdsAsync(userId.Value, ct);
            if (sourceIds.Count == 0)
            {
                userIds = [userId.Value];
            }
            else
            {
                var combined = new List<Guid>(sourceIds.Count + 1);
                combined.AddRange(sourceIds);
                combined.Add(userId.Value);
                userIds = combined;
            }
        }

        var entries = await repo.GetFilteredEntriesAsync(entityType, entityId, userIds, actions, limit, ct);
        return entries.Select(ToSnapshot).ToList();
    }

    // ─── IUserDataContributor (GDPR export) ───

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        // Chain-follow merge tombstones for source-id-attributed rows.
        var sourceIds = await userService.GetMergedSourceIdsAsync(userId, ct);
        IReadOnlyList<AuditLogEntry> entries;
        if (sourceIds.Count == 0)
        {
            entries = await repo.GetAllForUserContributorAsync(userId, ct);
        }
        else
        {
            var allIds = new List<Guid>(sourceIds.Count + 1);
            allIds.AddRange(sourceIds);
            allIds.Add(userId);
            entries = await repo.GetAllForUserIdsContributorAsync(allIds, ct);
        }

        // Post-merge: source rows surface as Subject for the target (actor context anonymized via AnonymizeForMergeAsync).
        var shaped = entries.Select(a => new
        {
            a.Action,
            a.EntityType,
            OccurredAt = a.OccurredAt.ToIso8601(),
            Role = a.ActorUserId == userId ? "Actor" : "Subject"
        }).ToList();

        return [new UserDataSlice(GdprExportSections.AuditLog, shaped)];
    }

    public Task<IReadOnlySet<Guid>> GetEntityIdsForEntityTypeActionsAsync(
        string entityType,
        IReadOnlyList<AuditAction> actions,
        CancellationToken ct = default) =>
        repo.GetEntityIdsForEntityTypeActionsAsync(entityType, actions, ct);

    // ─── ILegacyGoogleSyncAuditReader (goes with the six Google columns) ───

    public async Task<IReadOnlyList<LegacyGoogleSyncAuditRow>> GetLegacyGoogleSyncRowsAsync(
        CancellationToken ct = default)
    {
        var entries = await repo.GetLegacyGoogleSyncEntriesAsync(ct);
        return entries
            .Select(e => new LegacyGoogleSyncAuditRow(
                e.Id,
                e.Action,
                e.OccurredAt,
                e.Description,
                e.ResourceId!.Value,
                e.RelatedEntityId,
                e.RelatedEntityType,
                e.UserEmail,
                e.Role,
                e.SyncSource,
                e.Success,
                e.ErrorMessage))
            .ToList();
    }
}
