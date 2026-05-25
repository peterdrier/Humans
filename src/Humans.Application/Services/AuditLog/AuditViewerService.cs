using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;

namespace Humans.Application.Services.AuditLog;

/// <summary>Read-side wrapper over <see cref="IAuditLogService"/> that resolves actor/subject/team names. No DB or caching.</summary>
public sealed class AuditViewerService(
    IAuditLogService auditLog,
    IUserServiceRead userService,
    ITeamService teamService,
    ITeamResourceService teamResourceService) : IAuditViewerService
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

    public async Task<IReadOnlyList<AuditEvent>> GetForResourceAsync(Guid resourceId, CancellationToken ct = default)
    {
        var entries = await auditLog.GetByResourceAsync(resourceId);
        return await ResolveAsync(entries, ct);
    }

    public async Task<IReadOnlyList<AuditEvent>> GetGoogleSyncForUserAsync(Guid userId, CancellationToken ct = default)
    {
        var entries = await auditLog.GetGoogleSyncByUserAsync(userId);
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
        IReadOnlyList<AuditLogEntry> entries, CancellationToken ct)
    {
        if (entries.Count == 0)
            return [];

        var (userIds, teamIds, resourceIds) = CollectIds(entries);
        var users = userIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await GetUserDisplayNamesAsync(userIds, ct);
        var teams = teamIds.Count == 0
            ? new Dictionary<Guid, (string Name, string Slug)>()
            : await GetTeamNamesAsync(teamIds, ct);
        var resources = resourceIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await teamResourceService.GetResourceNamesByIdsAsync(resourceIds, ct);

        var result = new List<AuditEvent>(entries.Count);
        foreach (var entry in entries)
            result.Add(Resolve(entry, users, teams, resources));
        return result;
    }

    private async Task<IReadOnlyList<AuditEvent>> ResolveAsync(
        IReadOnlyList<AuditLogEntrySnapshot> entries, CancellationToken ct)
    {
        if (entries.Count == 0)
            return [];

        var (userIds, teamIds, resourceIds) = CollectIds(entries);
        var users = userIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await GetUserDisplayNamesAsync(userIds, ct);
        var teams = teamIds.Count == 0
            ? new Dictionary<Guid, (string Name, string Slug)>()
            : await GetTeamNamesAsync(teamIds, ct);
        var resources = resourceIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await teamResourceService.GetResourceNamesByIdsAsync(resourceIds, ct);

        var result = new List<AuditEvent>(entries.Count);
        foreach (var entry in entries)
            result.Add(Resolve(entry, users, teams, resources));
        return result;
    }

    private static AuditEvent Resolve(
        AuditLogEntry entry,
        IReadOnlyDictionary<Guid, string> users,
        IReadOnlyDictionary<Guid, (string Name, string Slug)> teams,
        IReadOnlyDictionary<Guid, string> resources)
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
        string? resourceName = entry.ResourceId.HasValue && resources.TryGetValue(entry.ResourceId.Value, out var rn) ? rn : null;

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
            ResourceName: resourceName);
    }

    private static AuditEvent Resolve(
        AuditLogEntrySnapshot entry,
        IReadOnlyDictionary<Guid, string> users,
        IReadOnlyDictionary<Guid, (string Name, string Slug)> teams,
        IReadOnlyDictionary<Guid, string> resources)
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
        string? resourceName = entry.ResourceId.HasValue && resources.TryGetValue(entry.ResourceId.Value, out var rn) ? rn : null;

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
            ResourceName: resourceName);
    }

    private async Task<Dictionary<Guid, string>> GetUserDisplayNamesAsync(
        IReadOnlyList<Guid> userIds, CancellationToken ct)
    {
        // BurnerName is the display name (memory/architecture/burnername-is-the-display-name.md). Users without one are absent.
        var users = await userService.GetUserInfosAsync(userIds, ct);
        return users
            .Where(kvp => !string.IsNullOrWhiteSpace(kvp.Value.Profile?.BurnerName))
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Profile!.BurnerName!);
    }

    private async Task<Dictionary<Guid, (string Name, string Slug)>> GetTeamNamesAsync(
        IReadOnlyList<Guid> teamIds, CancellationToken ct)
    {
        var teams = await teamService.GetByIdsWithParentsAsync(teamIds, ct);
        return teams.ToDictionary(kvp => kvp.Key, kvp => (kvp.Value.Name, kvp.Value.Slug));
    }

    private static (List<Guid> UserIds, List<Guid> TeamIds, List<Guid> ResourceIds) CollectIds(IReadOnlyList<AuditLogEntry> entries)
    {
        var users = new HashSet<Guid>();
        var teams = new HashSet<Guid>();
        var resources = new HashSet<Guid>();
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

            if (e.ResourceId.HasValue)
                resources.Add(e.ResourceId.Value);
        }
        return (users.ToList(), teams.ToList(), resources.ToList());
    }

    private static (List<Guid> UserIds, List<Guid> TeamIds, List<Guid> ResourceIds) CollectIds(IReadOnlyList<AuditLogEntrySnapshot> entries)
    {
        var users = new HashSet<Guid>();
        var teams = new HashSet<Guid>();
        var resources = new HashSet<Guid>();
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

            if (e.ResourceId.HasValue)
                resources.Add(e.ResourceId.Value);
        }
        return (users.ToList(), teams.ToList(), resources.ToList());
    }

    private static Guid? ResolveSubjectId(AuditLogEntry e)
    {
        // Subject = person acted upon. Guid.Empty → null (e.g. unmatched WorkspaceAccount addresses).
        Guid? subject = e.EntityType is "User" or "Profile" or "WorkspaceAccount"
            ? e.EntityId
            : string.Equals(e.RelatedEntityType, "User", StringComparison.Ordinal)
                ? e.RelatedEntityId
                : null;
        return subject == Guid.Empty ? null : subject;
    }

    private static Guid? ResolveSubjectId(AuditLogEntrySnapshot e)
    {
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

    private static Guid? ResolveTargetTeamId(AuditLogEntrySnapshot e) =>
        string.Equals(e.EntityType, "Team", StringComparison.Ordinal) ? e.EntityId
        : string.Equals(e.RelatedEntityType, "Team", StringComparison.Ordinal) ? e.RelatedEntityId
        : null;
}
