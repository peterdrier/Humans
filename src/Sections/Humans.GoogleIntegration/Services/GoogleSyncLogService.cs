using Humans.Base.Enums;
using Humans.Gdpr.Contracts;
using Humans.GoogleIntegration.Contracts;
using Humans.GoogleIntegration.Data;
using Humans.GoogleIntegration.Domain;
using Humans.Users.Contracts;
using NodaTime;

namespace Humans.GoogleIntegration.Services;

/// <summary>Write and read sides of <c>google_sync_log</c>; one class, three interfaces.</summary>
internal sealed class GoogleSyncLogService(
    IGoogleSyncLogRepository repo,
    ITeamResourceService teamResourceService,
    IUserServiceRead userService,
    IClock clock,
    ILogger<GoogleSyncLogService> logger)
    : IGoogleSyncLogService, IGoogleSyncLogViewer, IUserDataContributor
{
    public async Task LogAsync(
        GoogleSyncLogAction action,
        Guid resourceId,
        string description,
        string jobName,
        string userEmail,
        string role,
        GoogleSyncSource source,
        bool success,
        string? errorMessage = null,
        Guid? userId = null,
        CancellationToken ct = default)
    {
        var entry = new GoogleSyncLogEntry
        {
            Id = Guid.NewGuid(),
            Action = action,
            ResourceId = resourceId,
            UserId = userId,
            UserEmail = userEmail,
            Role = role,
            Source = source,
            Success = success,
            ErrorMessage = errorMessage,
            Description = $"{jobName}: {description}",
            JobName = jobName,
            OccurredAt = clock.GetCurrentInstant()
        };

        try
        {
            await repo.AddAsync(entry, ct);
        }
        catch (Exception ex)
        {
            // Best-effort: recording must never break the sync it describes.
            logger.LogError(ex, "Failed to persist Google sync log entry {EntryId} ({Action} on resource {ResourceId})",
                entry.Id, action, resourceId);
        }

        logger.LogInformation(
            "Google sync: {Action} {Role} for {Email} on resource {ResourceId} ({Source}, Success={Success})",
            action, role, userEmail, resourceId, source, success);
    }

    public async Task<IReadOnlyList<GoogleSyncLogView>> GetForResourceAsync(
        Guid resourceId, CancellationToken ct = default)
    {
        var entries = await repo.GetByResourceAsync(resourceId, ct);
        return await ToViewsAsync(entries, ct);
    }

    public async Task<IReadOnlyList<GoogleSyncLogView>> GetForUserAsync(
        Guid userId, CancellationToken ct = default)
    {
        var entries = await repo.GetByUserIdsAsync(await UserIdsWithMergedSourcesAsync(userId, ct), ct);
        return await ToViewsAsync(entries, ct);
    }

    /// <summary>GDPR Article 15 slice — every sync row attributed to this human, uncapped.</summary>
    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var entries = await repo.GetAllByUserIdsContributorAsync(
            await UserIdsWithMergedSourcesAsync(userId, ct), ct);
        return [new UserDataSlice(GdprExportSections.GoogleSyncLog, await ToViewsAsync(entries, ct))];
    }

    /// <summary>Chain-follows merge tombstones so a merged human keeps their trail.</summary>
    private async Task<List<Guid>> UserIdsWithMergedSourcesAsync(Guid userId, CancellationToken ct)
    {
        var sourceIds = await userService.GetMergedSourceIdsAsync(userId, ct);
        var ids = new List<Guid>(sourceIds.Count + 1) { userId };
        ids.AddRange(sourceIds);
        return ids;
    }

    private async Task<IReadOnlyList<GoogleSyncLogView>> ToViewsAsync(
        IReadOnlyList<GoogleSyncLogEntry> entries, CancellationToken ct)
    {
        if (entries.Count == 0)
            return [];

        var names = await teamResourceService.GetResourceNamesByIdsAsync(
            entries.Select(e => e.ResourceId).Distinct().ToList(), ct);

        return entries
            .Select(e => new GoogleSyncLogView(
                e.Action,
                e.OccurredAt,
                e.Description,
                names.TryGetValue(e.ResourceId, out var name) ? name : null,
                e.UserEmail,
                e.Role,
                e.Source,
                e.Success,
                e.ErrorMessage))
            .ToList();
    }
}
