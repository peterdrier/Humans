using Humans.Application.Interfaces.AuditLog;
using Humans.Domain.Entities;

namespace Humans.Application.Services.AuditLog;

/// <summary>
/// <inheritdoc cref="IAuditViewerService"/>
/// </summary>
/// <remarks>
/// Pure read-side service — wraps <see cref="IAuditLogService"/> for the raw
/// queries and uses its
/// <see cref="IAuditLogService.GetUserDisplayNamesAsync"/> /
/// <see cref="IAuditLogService.GetTeamNamesAsync"/> batch lookups for actor,
/// subject, and target-team name resolution. No DB access, no caching, no
/// merge-chain logic of its own — those concerns stay in
/// <see cref="IAuditLogService"/> where the audit-log table is owned.
/// </remarks>
public sealed class AuditViewerService : IAuditViewerService
{
    private readonly IAuditLogService _auditLog;

    public AuditViewerService(IAuditLogService auditLog)
    {
        _auditLog = auditLog;
    }

    public async Task<IReadOnlyList<AuditEvent>> GetRecentAsync(int count, CancellationToken ct = default)
    {
        var entries = await _auditLog.GetRecentAsync(count, ct);
        return await ResolveAsync(entries, ct);
    }

    public async Task<IReadOnlyList<AuditEvent>> GetForUserAsync(Guid userId, int count, CancellationToken ct = default)
    {
        var entries = await _auditLog.GetByUserAsync(userId, count, ct);
        return await ResolveAsync(entries, ct);
    }

    public async Task<IReadOnlyList<AuditEvent>> GetForResourceAsync(Guid resourceId, CancellationToken ct = default)
    {
        var entries = await _auditLog.GetByResourceAsync(resourceId);
        return await ResolveAsync(entries, ct);
    }

    public async Task<IReadOnlyList<AuditEvent>> GetGoogleSyncForUserAsync(Guid userId, CancellationToken ct = default)
    {
        var entries = await _auditLog.GetGoogleSyncByUserAsync(userId);
        return await ResolveAsync(entries, ct);
    }

    public async Task<IReadOnlyList<AuditEvent>> GetFilteredAsync(
        string? entityType,
        Guid? entityId,
        Guid? userId,
        IReadOnlyList<Domain.Enums.AuditAction>? actions,
        int limit,
        CancellationToken ct = default)
    {
        var entries = await _auditLog.GetFilteredEntriesAsync(entityType, entityId, userId, actions, limit, ct);
        return await ResolveAsync(entries, ct);
    }

    public async Task<AuditEventPage> GetPageAsync(string? actionFilter, int page, int pageSize, CancellationToken ct = default)
    {
        // Reuse the existing single-trip page query so we get items + counts
        // + already-batched name lookups in one round-trip; we just rewrap
        // the result into resolved AuditEvent shape so view-layer callers
        // never see the raw entry / dictionary pair.
        var raw = await _auditLog.GetAuditLogPageAsync(actionFilter, page, pageSize, ct);
        var events = raw.Items
            .Select(e => Resolve(e, raw.UserDisplayNames, raw.TeamNames))
            .ToList();
        return new AuditEventPage(events, raw.TotalCount, raw.AnomalyCount);
    }

    // ==========================================================================
    // Resolution
    // ==========================================================================

    private async Task<IReadOnlyList<AuditEvent>> ResolveAsync(
        IReadOnlyList<AuditLogEntry> entries, CancellationToken ct)
    {
        if (entries.Count == 0)
            return Array.Empty<AuditEvent>();

        var (userIds, teamIds) = CollectIds(entries);
        var users = userIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await _auditLog.GetUserDisplayNamesAsync(userIds, ct);
        var teams = teamIds.Count == 0
            ? new Dictionary<Guid, (string Name, string Slug)>()
            : await _auditLog.GetTeamNamesAsync(teamIds, ct);

        var result = new List<AuditEvent>(entries.Count);
        foreach (var entry in entries)
            result.Add(Resolve(entry, users, teams));
        return result;
    }

    private static AuditEvent Resolve(
        AuditLogEntry entry,
        IReadOnlyDictionary<Guid, string> users,
        IReadOnlyDictionary<Guid, (string Name, string Slug)> teams)
    {
        var subjectId = ResolveSubjectId(entry);
        var targetTeamId = ResolveTargetTeamId(entry);

        string? actorName = entry.ActorUserId.HasValue && users.TryGetValue(entry.ActorUserId.Value, out var an) ? an : null;
        string? subjectName = subjectId.HasValue && users.TryGetValue(subjectId.Value, out var sn) ? sn : null;
        string? teamName = null;
        string? teamSlug = null;
        if (targetTeamId.HasValue && teams.TryGetValue(targetTeamId.Value, out var team))
        {
            teamName = team.Name;
            teamSlug = team.Slug;
        }

        return new AuditEvent(
            Id: entry.Id,
            OccurredAt: entry.OccurredAt,
            Action: entry.Action,
            ActorUserId: entry.ActorUserId,
            ActorDisplayName: actorName,
            EntityType: entry.EntityType,
            EntityId: entry.EntityId,
            SubjectUserId: subjectId,
            SubjectDisplayName: subjectName,
            TargetTeamId: targetTeamId,
            TargetTeamName: teamName,
            TargetTeamSlug: teamSlug,
            RelatedEntityId: entry.RelatedEntityId,
            RelatedEntityType: entry.RelatedEntityType,
            Description: entry.Description,
            Role: entry.Role,
            UserEmail: entry.UserEmail,
            Success: entry.Success,
            ErrorMessage: entry.ErrorMessage,
            SyncSource: entry.SyncSource,
            ResourceId: entry.ResourceId,
            ResourceName: entry.Resource?.Name);
    }

    private static (List<Guid> UserIds, List<Guid> TeamIds) CollectIds(IReadOnlyList<AuditLogEntry> entries)
    {
        var users = new HashSet<Guid>();
        var teams = new HashSet<Guid>();
        foreach (var e in entries)
        {
            if (e.ActorUserId.HasValue)
                users.Add(e.ActorUserId.Value);

            var subjectId = ResolveSubjectId(e);
            if (subjectId.HasValue)
                users.Add(subjectId.Value);

            var teamId = ResolveTargetTeamId(e);
            if (teamId.HasValue)
                teams.Add(teamId.Value);
        }
        return (users.ToList(), teams.ToList());
    }

    private static Guid? ResolveSubjectId(AuditLogEntry e)
    {
        // Subject = the person acted upon. Guid.Empty means "no linked human"
        // (e.g. WorkspaceAccount audits where the address isn't matched to a
        // User); treat as null so callers don't render "Unknown".
        Guid? subject = e.EntityType is "User" or "Profile" or "WorkspaceAccount"
            ? e.EntityId
            : string.Equals(e.RelatedEntityType, "User", StringComparison.Ordinal)
                ? e.RelatedEntityId
                : null;
        return subject == Guid.Empty ? null : subject;
    }

    private static Guid? ResolveTargetTeamId(AuditLogEntry e) =>
        string.Equals(e.EntityType, "Team", StringComparison.Ordinal) ? e.EntityId
        : string.Equals(e.RelatedEntityType, "Team", StringComparison.Ordinal) ? e.RelatedEntityId
        : null;
}
