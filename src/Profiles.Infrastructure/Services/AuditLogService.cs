using Microsoft.Extensions.Logging;
using NodaTime;
using Profiles.Application.Interfaces;
using Profiles.Domain.Entities;
using Profiles.Domain.Enums;
using Profiles.Infrastructure.Data;

namespace Profiles.Infrastructure.Services;

/// <summary>
/// Records audit log entries by adding them to the DbContext.
/// Entries are NOT saved here — the caller's SaveChangesAsync persists them
/// atomically with the business operation.
/// </summary>
public class AuditLogService : IAuditLogService
{
    private readonly ProfilesDbContext _dbContext;
    private readonly IClock _clock;
    private readonly ILogger<AuditLogService> _logger;

    public AuditLogService(
        ProfilesDbContext dbContext,
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
}
