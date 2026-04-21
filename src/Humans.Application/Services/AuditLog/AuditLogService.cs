using Humans.Application.Extensions;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Application.Services.AuditLog;

/// <summary>
/// Application-layer implementation of <see cref="IAuditLogService"/>. Goes
/// through <see cref="IAuditLogRepository"/> for all data access — this type
/// never imports <c>Microsoft.EntityFrameworkCore</c>, enforced by
/// <c>Humans.Application.csproj</c>'s reference graph.
/// </summary>
/// <remarks>
/// <para>
/// <c>audit_log</c> is append-only per design-rules §12 — this service only
/// appends entries; there is no update or delete path. Each <c>LogAsync</c>
/// call persists its entry immediately (auto-saved by the repository). The
/// prior pattern (adding to the caller's shared Scoped <c>DbContext</c> and
/// relying on a downstream <c>SaveChanges</c>) is gone; it is replaced by
/// per-call persistence, which matches how recently-migrated sections
/// (Profile, User, Governance, Budget, City Planning) write.
/// </para>
/// <para>
/// Implements <see cref="IUserDataContributor"/> so the GDPR export
/// orchestrator can assemble per-user audit slices without crossing the
/// section boundary.
/// </para>
/// </remarks>
public sealed class AuditLogService : IAuditLogService, IUserDataContributor
{
    private readonly IAuditLogRepository _repo;
    private readonly IClock _clock;
    private readonly ILogger<AuditLogService> _logger;

    public AuditLogService(
        IAuditLogRepository repo,
        IClock clock,
        ILogger<AuditLogService> logger)
    {
        _repo = repo;
        _clock = clock;
        _logger = logger;
    }

    // ==========================================================================
    // Writes — append-only
    // ==========================================================================

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
            OccurredAt = _clock.GetCurrentInstant(),
            ActorUserId = null,
            RelatedEntityId = relatedEntityId,
            RelatedEntityType = relatedEntityType
        };

        await _repo.AddAsync(entry);

        _logger.LogInformation("Audit: {Action} on {EntityType} {EntityId} by {Actor} — {Description}",
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
            OccurredAt = _clock.GetCurrentInstant(),
            ActorUserId = actorUserId,
            RelatedEntityId = relatedEntityId,
            RelatedEntityType = relatedEntityType
        };

        await _repo.AddAsync(entry);

        _logger.LogInformation("Audit: {Action} on {EntityType} {EntityId} by user {ActorUserId} — {Description}",
            action, entityType, entityId, actorUserId, description);
    }

    /// <inheritdoc />
    public async Task LogGoogleSyncAsync(AuditAction action, Guid resourceId,
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

        await _repo.AddAsync(entry);

        _logger.LogInformation(
            "Audit: {Action} {Role} for {Email} on resource {ResourceId} ({Source}, Success={Success})",
            action, role, userEmail, resourceId, source, success);
    }

    // ==========================================================================
    // Reads
    // ==========================================================================

    /// <inheritdoc />
    public Task<IReadOnlyList<AuditLogEntry>> GetByResourceAsync(Guid resourceId) =>
        _repo.GetByResourceAsync(resourceId);

    /// <inheritdoc />
    public Task<IReadOnlyList<AuditLogEntry>> GetGoogleSyncByUserAsync(Guid userId) =>
        _repo.GetGoogleSyncByUserAsync(userId);

    /// <inheritdoc />
    public Task<IReadOnlyList<AuditLogEntry>> GetRecentAsync(int count, CancellationToken ct = default) =>
        _repo.GetRecentAsync(count, ct);

    /// <inheritdoc />
    public Task<(IReadOnlyList<AuditLogEntry> Items, int TotalCount, int AnomalyCount)> GetFilteredAsync(
        string? actionFilter, int page, int pageSize, CancellationToken ct = default)
    {
        AuditAction? parsed = null;
        if (!string.IsNullOrWhiteSpace(actionFilter) &&
            Enum.TryParse<AuditAction>(actionFilter, ignoreCase: true, out var action))
        {
            parsed = action;
        }

        return _repo.GetFilteredAsync(parsed, page, pageSize, ct);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<AuditLogEntry>> GetByUserAsync(Guid userId, int count, CancellationToken ct = default) =>
        _repo.GetByUserAsync(userId, count, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<AuditLogEntry>> GetFilteredEntriesAsync(
        string? entityType = null,
        Guid? entityId = null,
        Guid? userId = null,
        IReadOnlyList<AuditAction>? actions = null,
        int limit = 20,
        CancellationToken ct = default) =>
        _repo.GetFilteredEntriesAsync(entityType, entityId, userId, actions, limit, ct);

    /// <inheritdoc />
    public async Task<AuditLogPageResult> GetAuditLogPageAsync(
        string? actionFilter, int page, int pageSize, CancellationToken ct = default)
    {
        var (items, totalCount, anomalyCount) = await GetFilteredAsync(actionFilter, page, pageSize, ct);

        // Collect all user and team IDs that might appear as actors, subjects, or targets.
        var userIds = new HashSet<Guid>();
        var teamIds = new HashSet<Guid>();

        foreach (var e in items)
        {
            if (e.ActorUserId.HasValue)
                userIds.Add(e.ActorUserId.Value);

            // Subject user ID: User/Profile/WorkspaceAccount entity types, or related User.
            if (e.EntityType is "User" or "Profile" or "WorkspaceAccount")
                userIds.Add(e.EntityId);
            else if (string.Equals(e.RelatedEntityType, "User", StringComparison.Ordinal) && e.RelatedEntityId.HasValue)
                userIds.Add(e.RelatedEntityId.Value);

            // Target team ID.
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
    public Task<Dictionary<Guid, string>> GetUserDisplayNamesAsync(
        IReadOnlyList<Guid> userIds, CancellationToken ct = default) =>
        _repo.GetUserDisplayNamesAsync(userIds, ct);

    /// <inheritdoc />
    public Task<Dictionary<Guid, (string Name, string Slug)>> GetTeamNamesAsync(
        IReadOnlyList<Guid> teamIds, CancellationToken ct = default) =>
        _repo.GetTeamNamesAsync(teamIds, ct);

    // ==========================================================================
    // IUserDataContributor (GDPR export)
    // ==========================================================================

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var entries = await _repo.GetAllForUserContributorAsync(userId, ct);

        var shaped = entries.Select(a => new
        {
            a.Action,
            a.EntityType,
            OccurredAt = a.OccurredAt.ToInvariantInstantString(),
            Role = a.ActorUserId == userId ? "Actor" : "Subject"
        }).ToList();

        return [new UserDataSlice(GdprExportSections.AuditLog, shaped)];
    }
}
