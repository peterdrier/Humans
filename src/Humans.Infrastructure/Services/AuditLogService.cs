using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;

namespace Humans.Infrastructure.Services;

/// <summary>
/// Records audit log entries by adding them to the DbContext.
/// Entries are NOT saved here — the caller's SaveChangesAsync persists them
/// atomically with the business operation.
/// </summary>
public class AuditLogService : IAuditLogService
{
    private readonly HumansDbContext _dbContext;
    private readonly IClock _clock;
    private readonly ILogger<AuditLogService> _logger;

    public AuditLogService(
        HumansDbContext dbContext,
        IClock clock,
        ILogger<AuditLogService> logger)
    {
        _dbContext = dbContext;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task LogAsync(AuditAction action, string entityType, Guid entityId,
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
            OccurredAt = _clock.GetCurrentInstant(),
            ActorUserId = null,
            RelatedEntityId = relatedEntityId,
            RelatedEntityType = relatedEntityType
        };

        _dbContext.AuditLogEntries.Add(entry);

        _logger.LogInformation("Audit: {Action} on {EntityType} {EntityId} by {Actor} — {Description}",
            action, entityType, entityId, jobName, description);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task LogAsync(AuditAction action, string entityType, Guid entityId,
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
            OccurredAt = _clock.GetCurrentInstant(),
            ActorUserId = actorUserId,
            RelatedEntityId = relatedEntityId,
            RelatedEntityType = relatedEntityType
        };

        _dbContext.AuditLogEntries.Add(entry);

        _logger.LogInformation("Audit: {Action} on {EntityType} {EntityId} by user {ActorUserId} — {Description}",
            action, entityType, entityId, actorUserId, description);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task LogGoogleSyncAsync(AuditAction action, Guid resourceId,
        string description, string jobName,
        string userEmail, string role, GoogleSyncSource source, bool success,
        string? errorMessage = null,
        Guid? relatedEntityId = null, string? relatedEntityType = null)
    {
        var entry = new AuditLogEntry
        {
            Id = Guid.NewGuid(),
            Action = action,
            EntityType = "GoogleResource",
            EntityId = resourceId,
            Description = $"{jobName}: {description}",
            OccurredAt = _clock.GetCurrentInstant(),
            ActorUserId = null,
            RelatedEntityId = relatedEntityId,
            RelatedEntityType = relatedEntityType,
            ResourceId = resourceId,
            Success = success,
            ErrorMessage = errorMessage,
            Role = role,
            SyncSource = source,
            UserEmail = userEmail
        };

        _dbContext.AuditLogEntries.Add(entry);

        _logger.LogInformation(
            "Audit: {Action} {Role} for {Email} on resource {ResourceId} ({Source}, Success={Success})",
            action, role, userEmail, resourceId, source, success);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AuditLogEntry>> GetByResourceAsync(Guid resourceId)
    {
        return await _dbContext.AuditLogEntries
            .AsNoTracking()
            .Where(e => e.ResourceId == resourceId)
            .OrderByDescending(e => e.OccurredAt)
            .Take(200)
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AuditLogEntry>> GetGoogleSyncByUserAsync(Guid userId)
    {
        return await _dbContext.AuditLogEntries
            .AsNoTracking()
            .Include(e => e.Resource)
            .Where(e => e.ResourceId != null && e.RelatedEntityId == userId)
            .OrderByDescending(e => e.OccurredAt)
            .Take(200)
            .ToListAsync();
    }

    public async Task<IReadOnlyList<AuditLogEntry>> GetRecentAsync(int count, CancellationToken ct = default)
    {
        return await _dbContext.AuditLogEntries
            .AsNoTracking()
            .OrderByDescending(e => e.OccurredAt)
            .Take(count)
            .ToListAsync(ct);
    }

    public async Task<(IReadOnlyList<AuditLogEntry> Items, int TotalCount, int AnomalyCount)> GetFilteredAsync(
        string? actionFilter, int page, int pageSize, CancellationToken ct = default)
    {
        var query = _dbContext.AuditLogEntries.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(actionFilter) && Enum.TryParse<AuditAction>(actionFilter, ignoreCase: true, out var actionEnum))
        {
            query = query.Where(e => e.Action == actionEnum);
        }

        var totalCount = await query.CountAsync(ct);

        var items = await query
            .OrderByDescending(e => e.OccurredAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        var anomalyCount = await _dbContext.AuditLogEntries
            .AsNoTracking()
            .CountAsync(e => e.Action == AuditAction.AnomalousPermissionDetected, ct);

        return (items, totalCount, anomalyCount);
    }

    public async Task<IReadOnlyList<AuditLogEntry>> GetByUserAsync(Guid userId, int count, CancellationToken ct = default)
    {
        return await _dbContext.AuditLogEntries
            .AsNoTracking()
            .Where(e =>
                (e.EntityType == "User" && e.EntityId == userId) ||
                (e.RelatedEntityId == userId))
            .OrderByDescending(e => e.OccurredAt)
            .Take(count)
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task<AuditLogPageResult> GetAuditLogPageAsync(
        string? actionFilter, int page, int pageSize, CancellationToken ct = default)
    {
        var (items, totalCount, anomalyCount) = await GetFilteredAsync(actionFilter, page, pageSize, ct);

        // Collect all user IDs that might appear as actors or subjects
        var userIds = new HashSet<Guid>();
        var teamIds = new HashSet<Guid>();

        foreach (var e in items)
        {
            if (e.ActorUserId.HasValue)
                userIds.Add(e.ActorUserId.Value);

            // Subject user ID: User/Profile/WorkspaceAccount entity types, or related User
            if (e.EntityType is "User" or "Profile" or "WorkspaceAccount")
                userIds.Add(e.EntityId);
            else if (string.Equals(e.RelatedEntityType, "User", StringComparison.Ordinal) && e.RelatedEntityId.HasValue)
                userIds.Add(e.RelatedEntityId.Value);

            // Target team ID
            if (string.Equals(e.EntityType, "Team", StringComparison.Ordinal))
                teamIds.Add(e.EntityId);
            else if (string.Equals(e.RelatedEntityType, "Team", StringComparison.Ordinal) && e.RelatedEntityId.HasValue)
                teamIds.Add(e.RelatedEntityId.Value);
        }

        var userDisplayNames = await GetUserDisplayNamesAsync(userIds.ToList(), ct);
        var teamNameLookup = await GetTeamNamesAsync(teamIds.ToList(), ct);

        return new AuditLogPageResult(items, totalCount, anomalyCount, userDisplayNames, teamNameLookup);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AuditLogEntry>> GetFilteredEntriesAsync(
        string? entityType = null,
        Guid? entityId = null,
        Guid? userId = null,
        IReadOnlyList<AuditAction>? actions = null,
        int limit = 20,
        CancellationToken ct = default)
    {
        var query = _dbContext.AuditLogEntries.AsNoTracking().AsQueryable();

        if (!string.IsNullOrEmpty(entityType))
            query = query.Where(e => e.EntityType == entityType);

        if (entityId.HasValue)
            query = query.Where(e => e.EntityId == entityId.Value);

        if (userId.HasValue)
            query = query.Where(e =>
                e.ActorUserId == userId.Value ||
                e.RelatedEntityId == userId.Value ||
                (e.EntityType == "User" && e.EntityId == userId.Value));

        if (actions is { Count: > 0 })
            query = query.Where(e => actions.Contains(e.Action));

        return await query
            .OrderByDescending(e => e.OccurredAt)
            .Take(limit)
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task<Dictionary<Guid, string>> GetUserDisplayNamesAsync(IReadOnlyList<Guid> userIds, CancellationToken ct = default)
    {
        if (userIds.Count == 0)
            return new Dictionary<Guid, string>();

        return await _dbContext.Users.AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
    }

    /// <inheritdoc />
    public async Task<Dictionary<Guid, (string Name, string Slug)>> GetTeamNamesAsync(IReadOnlyList<Guid> teamIds, CancellationToken ct = default)
    {
        if (teamIds.Count == 0)
            return new Dictionary<Guid, (string Name, string Slug)>();

        return await _dbContext.Teams.AsNoTracking()
            .Where(t => teamIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => (t.Name, t.Slug), ct);
    }
}
