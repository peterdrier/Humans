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
            Description = description,
            OccurredAt = _clock.GetCurrentInstant(),
            ActorUserId = null,
            ActorName = jobName,
            RelatedEntityId = relatedEntityId,
            RelatedEntityType = relatedEntityType
        };

        _dbContext.AuditLogEntries.Add(entry);

        _logger.LogDebug("Audit: {Action} on {EntityType} {EntityId} by {Actor} — {Description}",
            action, entityType, entityId, jobName, description);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task LogAsync(AuditAction action, string entityType, Guid entityId,
        string description, Guid actorUserId, string actorDisplayName,
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
            ActorName = $"Admin: {actorDisplayName}",
            RelatedEntityId = relatedEntityId,
            RelatedEntityType = relatedEntityType
        };

        _dbContext.AuditLogEntries.Add(entry);

        _logger.LogDebug("Audit: {Action} on {EntityType} {EntityId} by {Actor} — {Description}",
            action, entityType, entityId, $"Admin: {actorDisplayName}", description);

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
            Description = description,
            OccurredAt = _clock.GetCurrentInstant(),
            ActorUserId = null,
            ActorName = jobName,
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

        _logger.LogDebug(
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
}
