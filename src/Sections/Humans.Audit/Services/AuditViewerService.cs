using Humans.Audit.Contracts;
using Humans.Base.Interfaces;

namespace Humans.Audit.Services;

internal sealed class AuditViewerService(
    IAuditLogReader auditLog,
    AuditActionRegistry registry,
    IEnumerable<IEntityNameContributor> nameContributors) : IAuditViewerService
{
    public async Task<IReadOnlyList<AuditEntry>> GetForUserAsync(Guid userId, int count, CancellationToken ct = default)
    {
        var entries = await auditLog.GetByUserAsync(userId, count, ct);
        return await ResolveAsync(entries, ct);
    }

    public async Task<IReadOnlyList<AuditEntry>> GetFilteredAsync(
        string? subjectType,
        Guid? subjectId,
        Guid? userId,
        IReadOnlyList<string>? actions,
        int limit,
        bool includeTargets = false,
        CancellationToken ct = default)
    {
        var entries = await auditLog.GetFilteredEntriesAsync(subjectType, subjectId, userId, actions, limit, includeTargets, ct);
        return await ResolveAsync(entries, ct);
    }

    /// <summary><paramref name="actionFilter"/> is a case-insensitive match on a declared action name.</summary>
    public async Task<AuditEntryPage> GetPageAsync(string? actionFilter, int page, int pageSize, CancellationToken ct = default)
    {
        var (items, totalCount, alertCounts) = await auditLog.GetFilteredAsync(actionFilter, page, pageSize, ct);
        var events = await ResolveAsync(items, ct);
        return new AuditEntryPage(events, totalCount, alertCounts);
    }

    private async Task<IReadOnlyList<AuditEntry>> ResolveAsync(
        IReadOnlyList<AuditLogEntrySnapshot> entries, CancellationToken ct)
    {
        if (entries.Count == 0)
            return [];

        var names = await ResolveNamesAsync(CollectIds(entries), ct);

        var result = new List<AuditEntry>(entries.Count);
        foreach (var entry in entries)
            result.Add(Resolve(entry, names));
        return result;
    }

    /// <summary>Each contributor answers for the ids it owns; the first answer for an id wins.</summary>
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

    private AuditEntry Resolve(
        AuditLogEntrySnapshot entry,
        IReadOnlyDictionary<Guid, EntityName> names)
    {
        var personId = ResolvePersonId(entry);
        var teamId = ResolveTeamId(entry);

        return new AuditEntry(
            Id: entry.Id,
            OccurredAt: entry.OccurredAt,
            Action: registry.Resolve(entry.Action),
            ActorUserId: entry.ActorUserId,
            ActorDisplayName: NameOf(entry.ActorUserId, names),
            SubjectType: entry.SubjectType,
            SubjectId: entry.SubjectId,
            PersonUserId: personId,
            PersonDisplayName: NameOf(personId, names),
            TeamId: teamId,
            TeamName: NameOf(teamId, names),
            TeamSlug: teamId.HasValue && names.TryGetValue(teamId.Value, out var team)
                ? team.Slug
                : null,
            TargetId: entry.TargetId,
            TargetType: entry.TargetType,
            Description: entry.Description,
            JobName: entry.JobName);
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

            var personId = ResolvePersonId(e);
            if (personId.HasValue)
                ids.Add(personId.Value);

            var teamId = ResolveTeamId(e);
            if (teamId.HasValue)
                ids.Add(teamId.Value);
        }
        return ids;
    }

    private static Guid? ResolvePersonId(AuditLogEntrySnapshot e)
    {
        // Guid.Empty is an unmatched address (e.g. WorkspaceAccount): no person.
        Guid? person = e.SubjectType is AuditSubjectTypes.User or "Profile" or "WorkspaceAccount"
            ? e.SubjectId
            : string.Equals(e.TargetType, AuditSubjectTypes.User, StringComparison.Ordinal)
                ? e.TargetId
                : null;
        return person == Guid.Empty ? null : person;
    }

    private static Guid? ResolveTeamId(AuditLogEntrySnapshot e) =>
        string.Equals(e.SubjectType, "Team", StringComparison.Ordinal) ? e.SubjectId
        : string.Equals(e.TargetType, "Team", StringComparison.Ordinal) ? e.TargetId
        : null;
}

/// <summary><c>AlertCounts</c> is rows per <see cref="AuditActionDefinition.Alert"/> action over the whole log, keyed by name.</summary>
internal sealed record AuditEntryPage(
    IReadOnlyList<AuditEntry> Items,
    int TotalCount,
    IReadOnlyDictionary<string, int> AlertCounts);
