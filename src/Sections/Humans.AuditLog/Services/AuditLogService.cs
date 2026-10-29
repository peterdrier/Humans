using Humans.Base.Attributes;
using Humans.Base.Extensions;
using Humans.AuditLog.Contracts;
using Humans.AuditLog.Data;
using Humans.AuditLog.Domain;
using Humans.Gdpr.Contracts;
using NodaTime;
using Humans.Users.Contracts;

namespace Humans.AuditLog.Services;

[DontFix(
    reason: "Audit (crosscut) reads merged-account source IDs via IUserService so queries follow merged identities. Extracting merge-id resolution to a standalone leaf is deferred and Peter-owned.",
    since: "2026-05-25")]
internal sealed class AuditLogService(
    IAuditLogRepository repo,
    IUserService userService,
    AuditActionRegistry registry,
    IClock clock,
    ILogger<AuditLogService> logger)
    : IAuditLogService, IAuditLogReader, IUserDataContributor, ILegacyGoogleSyncAuditReader
{
    internal const string AuditLog = "AuditLog";

    /// <inheritdoc />
    public async Task LogAsync(AuditActionDefinition action, string entityType, Guid entityId,
        string description, string jobName,
        Guid? relatedEntityId = null, string? relatedEntityType = null)
    {
        var entry = new AuditLogEntry
        {
            Id = Guid.NewGuid(),
            Action = action.Name,
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
            action.Name, entityType, entityId, jobName, description);
    }

    /// <inheritdoc />
    public async Task LogAsync(AuditActionDefinition action, string entityType, Guid entityId,
        string description, Guid actorUserId,
        Guid? relatedEntityId = null, string? relatedEntityType = null)
    {
        var entry = new AuditLogEntry
        {
            Id = Guid.NewGuid(),
            Action = action.Name,
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
            action.Name, entityType, entityId, actorUserId, description);
    }

    /// <inheritdoc />
    public Task LogAsync(AuditAction action, string entityType, Guid entityId,
        string description, string jobName,
        Guid? relatedEntityId = null, string? relatedEntityType = null) =>
        LogAsync(Definition(action), entityType, entityId, description, jobName, relatedEntityId, relatedEntityType);

    /// <inheritdoc />
    public Task LogAsync(AuditAction action, string entityType, Guid entityId,
        string description, Guid actorUserId,
        Guid? relatedEntityId = null, string? relatedEntityType = null) =>
        LogAsync(Definition(action), entityType, entityId, description, actorUserId, relatedEntityId, relatedEntityType);

    private static AuditActionDefinition Definition(AuditAction action) => new(action.ToString());

    private static IReadOnlyList<string> Names(IReadOnlyList<AuditAction> actions) =>
        actions.Select(a => a.ToString()).ToList();

    private static IReadOnlyList<string> Names(IReadOnlyList<AuditActionDefinition> actions) =>
        actions.Select(a => a.Name).ToList();

    private async Task PersistAsync(AuditLogEntry entry)
    {
        try
        {
            await repo.AddAsync(entry);
        }
        catch (Exception ex)
        {
            // Best-effort: never break the business operation.
            logger.LogError(ex,
                "Failed to persist audit entry {EntryId} ({Action} on {EntityType} {EntityId})",
                entry.Id, entry.Action, entry.EntityType, entry.EntityId);
        }
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
    public async Task<(IReadOnlyList<AuditLogEntrySnapshot> Items, int TotalCount, IReadOnlyDictionary<string, int> AlertCounts)> GetFilteredAsync(
        string? actionFilter, int page, int pageSize, CancellationToken ct = default)
    {
        var filter = string.IsNullOrWhiteSpace(actionFilter) ? null : registry.CanonicalName(actionFilter.Trim());

        var (items, totalCount, alertCounts) = await repo.GetFilteredAsync(filter, registry.AlertNames, page, pageSize, ct);
        return (items.Select(ToSnapshot).ToList(), totalCount, alertCounts);
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
    public Task<IReadOnlyList<AuditLogEntrySnapshot>> GetFilteredEntriesAsync(
        string? entityType,
        Guid? entityId,
        Guid? userId,
        IReadOnlyList<AuditActionDefinition> actions,
        int limit = 20,
        CancellationToken ct = default) =>
        FilterByNamesAsync(entityType, entityId, userId, Names(actions), limit, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<AuditLogEntrySnapshot>> GetFilteredEntriesAsync(
        string? entityType = null,
        Guid? entityId = null,
        Guid? userId = null,
        IReadOnlyList<AuditAction>? actions = null,
        int limit = 20,
        CancellationToken ct = default) =>
        FilterByNamesAsync(entityType, entityId, userId, actions is null ? null : Names(actions), limit, ct);

    Task<IReadOnlyList<AuditLogEntrySnapshot>> IAuditLogReader.GetFilteredEntriesAsync(
        string? entityType,
        Guid? entityId,
        Guid? userId,
        IReadOnlyList<string>? actions,
        int limit,
        CancellationToken ct) =>
        FilterByNamesAsync(entityType, entityId, userId, actions, limit, ct);

    private async Task<IReadOnlyList<AuditLogEntrySnapshot>> FilterByNamesAsync(
        string? entityType,
        Guid? entityId,
        Guid? userId,
        IReadOnlyList<string>? actionNames,
        int limit,
        CancellationToken ct)
    {
        // Rows attributed to an account merged into this one belong to the same human; the
        // id list is the resolved record's, as in GetByUserAsync.
        IReadOnlyCollection<Guid>? userIds = null;
        if (userId.HasValue)
        {
            userIds = (await userService.GetUserInfoAsync(userId.Value, ct))?.AllUserIds ?? [userId.Value];
        }

        var entries = await repo.GetFilteredEntriesAsync(entityType, entityId, userIds, actionNames, limit, ct);
        return entries.Select(ToSnapshot).ToList();
    }

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
        IReadOnlyList<AuditActionDefinition> actions,
        CancellationToken ct = default) =>
        repo.GetEntityIdsForEntityTypeActionsAsync(entityType, Names(actions), ct);

    public Task<IReadOnlySet<Guid>> GetEntityIdsForEntityTypeActionsAsync(
        string entityType,
        IReadOnlyList<AuditAction> actions,
        CancellationToken ct = default) =>
        repo.GetEntityIdsForEntityTypeActionsAsync(entityType, Names(actions), ct);

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
