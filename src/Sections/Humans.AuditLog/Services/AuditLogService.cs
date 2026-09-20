using Humans.Base.Attributes;
using Humans.Base.Extensions;
using Humans.AuditLog.Contracts;
using Humans.AuditLog.Data;
using Humans.AuditLog.Domain;
using Humans.Base.Enums;
using Humans.Gdpr.Contracts;
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
    internal const string AuditLog = "AuditLog";

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
        // Rows attributed to an account merged into this one belong to the same human, and
        // the id asked with may itself be an archived one: the read resolves it forward, so
        // the ids are the resolved record's, not the caller's.
        var allIds = (await userService.GetUserInfoAsync(userId, ct))?.AllUserIds ?? [userId];
        if (allIds.Count == 1)
        {
            var entries = await repo.GetByUserAsync(allIds[0], count, ct);
            return entries.Select(ToSnapshot).ToList();
        }

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
        // Rows attributed to an account merged into this one belong to the same human; the
        // id list is the resolved record's, as in GetByUserAsync.
        IReadOnlyCollection<Guid>? userIds = null;
        if (userId.HasValue)
        {
            userIds = (await userService.GetUserInfoAsync(userId.Value, ct))?.AllUserIds ?? [userId.Value];
        }

        var entries = await repo.GetFilteredEntriesAsync(entityType, entityId, userIds, actions, limit, ct);
        return entries.Select(ToSnapshot).ToList();
    }

    // ─── IUserDataContributor (GDPR export) ───

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        // Rows attributed to an account merged into this one belong to the same human; the
        // id list is the resolved record's, as in GetByUserAsync.
        var allIds = (await userService.GetUserInfoAsync(userId, ct))?.AllUserIds ?? [userId];
        IReadOnlyList<AuditLogEntry> entries;
        if (allIds.Count == 1)
        {
            entries = await repo.GetAllForUserContributorAsync(allIds[0], ct);
        }
        else
        {
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

        return [new UserDataSlice(AuditLog, shaped)];
    }

    // ─── IUserDataContributor (GDPR erasure) ───

    private static readonly IReadOnlyDictionary<string, string?> Erasure =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [AuditLog] =
                "Retained: append-only record of processing activity (GDPR Art. 30) and the " +
                "evidence trail for the erasure itself (Art. 17(3)(b), Art. 5(1)(f)). Rows key " +
                "off a user id; the free-text description of an action may still quote an " +
                "identifier captured when that action happened (an email an admin verified, a " +
                "name on a merge), and is kept under the same basis — redacting it would " +
                "destroy the evidence value the retention exists for."
        };

    public IReadOnlyDictionary<string, string?> ErasureDeclaration => Erasure;

    public Task EraseForUserAsync(Guid userId, CancellationToken ct) => Task.CompletedTask;

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
