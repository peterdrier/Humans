using Humans.AuditLog.Contracts;
using Humans.Base.Interfaces;

namespace Humans.AuditLog.Services;

/// <summary>Read-side wrapper over <see cref="IAuditLogReader"/> that resolves actor/subject/team names. No DB or caching.</summary>
/// <remarks>
/// Names come from the <see cref="IEntityNameContributor"/> fan-out, so this section reaches
/// into none of the sections that own the ids it renders (nobodies-collective/Humans#1059).
/// </remarks>
internal sealed class AuditViewerService(
    IAuditLogReader auditLog,
    IEnumerable<IEntityNameContributor> nameContributors) : IAuditViewerService
{
    public async Task<IReadOnlyList<AuditEvent>> GetRecentAsync(int count, CancellationToken ct = default)
    {
        var entries = await auditLog.GetRecentAsync(count, ct);
        return await ResolveAsync(entries, ct);
    }

    public async Task<IReadOnlyList<AuditEvent>> GetForUserAsync(Guid userId, int count, CancellationToken ct = default)
    {
        var entries = await auditLog.GetByUserAsync(userId, count, ct);
        return await ResolveAsync(entries, ct);
    }

    public async Task<IReadOnlyList<AuditEvent>> GetFilteredAsync(
        string? entityType,
        Guid? entityId,
        Guid? userId,
        IReadOnlyList<AuditAction>? actions,
        int limit,
        CancellationToken ct = default)
    {
        var entries = await auditLog.GetFilteredEntriesAsync(entityType, entityId, userId, actions, limit, ct);
        return await ResolveAsync(entries, ct);
    }

    public async Task<AuditEventPage> GetPageAsync(string? actionFilter, int page, int pageSize, CancellationToken ct = default)
    {
        var (items, totalCount, anomalyCount) = await auditLog.GetFilteredAsync(actionFilter, page, pageSize, ct);
        var events = await ResolveAsync(items, ct);
        return new AuditEventPage(events, totalCount, anomalyCount);
    }

    // ─── Resolution ───

    private async Task<IReadOnlyList<AuditEvent>> ResolveAsync(
        IReadOnlyList<AuditLogEntrySnapshot> entries, CancellationToken ct)
    {
        if (entries.Count == 0)
            return [];

        var names = await ResolveNamesAsync(CollectIds(entries), ct);

        var result = new List<AuditEvent>(entries.Count);
        foreach (var entry in entries)
            result.Add(Resolve(entry, names));
        return result;
    }

    /// <summary>
    /// One pass over every registered contributor. Each answers for the ids it owns and
    /// ignores the rest; first answer for an id wins.
    /// </summary>
    private async Task<Dictionary<Guid, EntityName>> ResolveNamesAsync(
        IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        var resolved = new Dictionary<Guid, EntityName>();
        if (ids.Count == 0)
            return resolved;

        foreach (var contributor in nameContributors)
        {
            foreach (var (id, name) in await contributor.ResolveNamesAsync(ids, ct))
                resolved.TryAdd(id, name);
        }
        return resolved;
    }

    private static AuditEvent Resolve(
        AuditLogEntrySnapshot entry,
        IReadOnlyDictionary<Guid, EntityName> names)
    {
        var subjectId = ResolveSubjectId(entry);
        var targetTeamId = ResolveTargetTeamId(entry);

        return new AuditEvent(
            Id: entry.Id,
            OccurredAt: entry.OccurredAt,
            Action: entry.Action,
            ActorUserId: entry.ActorUserId,
            ActorDisplayName: NameOf(entry.ActorUserId, names),
            EntityType: entry.EntityType,
            EntityId: entry.EntityId,
            SubjectUserId: subjectId,
            SubjectDisplayName: NameOf(subjectId, names),
            TargetTeamId: targetTeamId,
            TargetTeamName: NameOf(targetTeamId, names),
            TargetTeamSlug: targetTeamId.HasValue && names.TryGetValue(targetTeamId.Value, out var team)
                ? team.Slug
                : null,
            RelatedEntityId: entry.RelatedEntityId,
            RelatedEntityType: entry.RelatedEntityType,
            Description: entry.Description);
    }

    private static string? NameOf(Guid? id, IReadOnlyDictionary<Guid, EntityName> names) =>
        id.HasValue && names.TryGetValue(id.Value, out var entry) ? entry.DisplayName : null;

    private static IReadOnlyCollection<Guid> CollectIds(IReadOnlyList<AuditLogEntrySnapshot> entries)
    {
        var ids = new HashSet<Guid>();
        foreach (var e in entries)
        {
            if (e.ActorUserId.HasValue)
                ids.Add(e.ActorUserId.Value);

            var subjectId = ResolveSubjectId(e);
            if (subjectId.HasValue)
                ids.Add(subjectId.Value);

            var teamId = ResolveTargetTeamId(e);
            if (teamId.HasValue)
                ids.Add(teamId.Value);
        }
        return ids;
    }

    private static Guid? ResolveSubjectId(AuditLogEntrySnapshot e)
    {
        // Subject = person acted upon. Guid.Empty → null (e.g. unmatched WorkspaceAccount addresses).
        Guid? subject = e.EntityType is "User" or "Profile" or "WorkspaceAccount"
            ? e.EntityId
            : string.Equals(e.RelatedEntityType, "User", StringComparison.Ordinal)
                ? e.RelatedEntityId
                : null;
        return subject == Guid.Empty ? null : subject;
    }

    private static Guid? ResolveTargetTeamId(AuditLogEntrySnapshot e) =>
        string.Equals(e.EntityType, "Team", StringComparison.Ordinal) ? e.EntityId
        : string.Equals(e.RelatedEntityType, "Team", StringComparison.Ordinal) ? e.RelatedEntityId
        : null;
}
