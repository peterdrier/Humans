using Humans.Base.Attributes;
using Humans.Base.Extensions;
using Humans.Base.Interfaces;
using Humans.Audit.Contracts;
using Humans.Audit.Data;
using Humans.Audit.Domain;
using Humans.Gdpr.Contracts;
using NodaTime;
using Humans.Users.Contracts;

namespace Humans.Audit.Services;

[DontFix(
    reason: "Audit (crosscut) reads merged-account source IDs via IUserService so queries follow merged identities. Extracting merge-id resolution to a standalone leaf is deferred and Peter-owned.",
    since: "2026-05-25")]
internal sealed class AuditLogService(
    IAuditLogRepository repo,
    IUserService userService,
    AuditActionRegistry registry,
    IClock clock,
    ILogger<AuditLogService> logger)
    : IAuditLogService, IAuditLogReader, IUserDataContributor, ILegacyGoogleSyncAuditReader, IRetentionPeriodContributor
{
    internal const string Audit = "Audit";
    internal const string AuditTemporary = "AuditTemporary";

    /// <inheritdoc />
    public async Task LogAsync(AuditActionDefinition action, string subjectType, Guid subjectId,
        string description, string jobName,
        Guid? targetId = null, string? targetType = null)
    {
        var entry = new AuditLogEntrySnapshot(Guid.NewGuid(), action.Name, subjectType, subjectId,
            description, clock.GetCurrentInstant(), ActorUserId: null,
            targetId, targetType, action.Kept, jobName);

        if (!await PersistAsync(entry)) return;

        logger.LogInformation("Audit: {Action} on {SubjectType} {SubjectId} by {Actor} — {Description}",
            action.Name, subjectType, subjectId, jobName, description);
    }

    /// <inheritdoc />
    public async Task LogAsync(AuditActionDefinition action, string subjectType, Guid subjectId,
        string description, Guid actorUserId,
        Guid? targetId = null, string? targetType = null)
    {
        var entry = new AuditLogEntrySnapshot(Guid.NewGuid(), action.Name, subjectType, subjectId,
            description, clock.GetCurrentInstant(), actorUserId,
            targetId, targetType, action.Kept);

        if (!await PersistAsync(entry)) return;

        logger.LogInformation("Audit: {Action} on {SubjectType} {SubjectId} by user {ActorUserId} — {Description}",
            action.Name, subjectType, subjectId, actorUserId, description);
    }

    private async Task<bool> PersistAsync(AuditLogEntrySnapshot entry)
    {
        try
        {
            await repo.AddAsync(entry);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Failed to persist audit entry {EntryId}: {Action} on {SubjectType} {SubjectId} at {OccurredAt} by user {ActorUserId}; related {TargetType} {TargetId}; {Description}",
                entry.Id, entry.Action, entry.SubjectType, entry.SubjectId, entry.OccurredAt, entry.ActorUserId,
                entry.TargetType, entry.TargetId, entry.Description);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<(IReadOnlyList<AuditLogEntrySnapshot> Items, int TotalCount, IReadOnlyDictionary<string, int> AlertCounts)> GetFilteredAsync(
        string? actionFilter, int page, int pageSize, CancellationToken ct = default)
    {
        var filter = string.IsNullOrWhiteSpace(actionFilter) ? null : registry.CanonicalName(actionFilter.Trim());

        var (items, totalCount, alertCounts) = await repo.GetFilteredAsync(filter, registry.AlertNames, page, pageSize, ct);
        return (items, totalCount, alertCounts);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AuditLogEntrySnapshot>> GetByUserAsync(Guid userId, int count, CancellationToken ct = default)
    {
        // Rows attributed to an account merged into this one belong to the same human, and
        // the id asked with may itself be an archived one: the read resolves it forward, so
        // the ids are the resolved record's, not the caller's.
        var allIds = (await userService.GetUserInfoAsync(userId, ct))?.AllUserIds ?? [userId];
        return await repo.GetByUserIdsAsync(allIds, count, ct);
    }

    public Task<IReadOnlyList<AuditLogEntrySnapshot>> GetFilteredEntriesAsync(
        string? subjectType,
        Guid? subjectId,
        Guid? userId,
        IReadOnlyList<string>? actions,
        int limit,
        bool includeTargets = false,
        CancellationToken ct = default) =>
        FilterByNamesAsync(subjectType, subjectId, userId, actions, limit, includeTargets, ct);

    private async Task<IReadOnlyList<AuditLogEntrySnapshot>> FilterByNamesAsync(
        string? subjectType,
        Guid? subjectId,
        Guid? userId,
        IReadOnlyList<string>? actionNames,
        int limit,
        bool includeTargets,
        CancellationToken ct)
    {
        IReadOnlyCollection<Guid>? userIds = null;
        if (userId.HasValue)
        {
            userIds = (await userService.GetUserInfoAsync(userId.Value, ct))?.AllUserIds ?? [userId.Value];
        }

        return await repo.GetFilteredEntriesAsync(subjectType, subjectId, userIds, actionNames, limit, includeTargets, ct);
    }

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var allIds = (await userService.GetUserInfoAsync(userId, ct))?.AllUserIds ?? [userId];
        var entries = await repo.GetAllForUserIdsContributorAsync(allIds, ct);

        bool IsActor(AuditLogEntrySnapshot a) => a.ActorUserId is { } actor && allIds.Contains(actor);
        string RoleOf(AuditLogEntrySnapshot a) => IsActor(a) ? "Actor" : allIds.Contains(a.SubjectId) ? "Subject" : "Target";

        // The description of an action this person took on someone else may quote that other person's data
        // (an email verified, a name on a merge), so only rows about this person carry it, and only on the
        // temporary side; the permanent slice keeps its pre-existing shape.
        var permanent = entries.Where(e => e.Kept == AuditKept.Forever).Select(a => new
        {
            a.Action,
            a.SubjectType,
            OccurredAt = a.OccurredAt.ToIso8601(),
            Role = RoleOf(a)
        }).ToList();
        var temporary = entries.Where(e => e.Kept == AuditKept.RetentionPeriod).Select(a => new
        {
            a.Action,
            a.SubjectType,
            Description = IsActor(a) ? null : a.Description,
            OccurredAt = a.OccurredAt.ToIso8601(),
            Role = RoleOf(a)
        }).ToList();

        return [new UserDataSlice(Audit, permanent), new UserDataSlice(AuditTemporary, temporary)];
    }

    private static readonly IReadOnlyDictionary<string, string?> Erasure =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [Audit] =
                "Retained: append-only record of processing activity (GDPR Art. 30) and the " +
                "evidence trail for the erasure itself (Art. 17(3)(b), Art. 5(1)(f)). Rows key " +
                "off a user id; the free-text description of an action may still quote an " +
                "identifier captured when that action happened (an email an admin verified, a " +
                "name on a merge), and is kept under the same basis — redacting it would " +
                "destroy the evidence value the retention exists for.",
            [AuditTemporary] = null
        };

    public IReadOnlyDictionary<string, string?> ErasureDeclaration => Erasure;

    /// <summary>Deletes only temporary entries: the permanent side is retained (see <see cref="ErasureDeclaration"/>).</summary>
    public Task EraseForUserAsync(Guid userId, CancellationToken ct) => repo.DeleteTemporaryForUserAsync(userId, ct);

    public string Label => "temporary audit entries";

    /// <summary>Temporary side only: the permanent table's triggers refuse every delete.</summary>
    public Task<int> DeleteOlderThanAsync(Instant cutoff, CancellationToken ct = default) =>
        repo.DeleteTemporaryBeforeAsync(cutoff, ct);

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
                e.TargetId,
                e.TargetType,
                e.UserEmail,
                e.Role,
                e.SyncSource,
                e.Success,
                e.ErrorMessage))
            .ToList();
    }
}
