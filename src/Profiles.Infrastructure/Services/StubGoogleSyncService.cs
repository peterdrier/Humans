using Microsoft.Extensions.Logging;
using Profiles.Application.Interfaces;
using Profiles.Domain.Entities;

namespace Profiles.Infrastructure.Services;

/// <summary>
/// Stub implementation of IGoogleSyncService that logs actions without calling Google APIs.
/// Replace with real implementation when Google Workspace API integration is ready.
/// </summary>
public class StubGoogleSyncService : IGoogleSyncService
{
    private readonly ILogger<StubGoogleSyncService> _logger;

    public StubGoogleSyncService(ILogger<StubGoogleSyncService> logger)
    {
        _logger = logger;
    }

    public Task<GoogleResource> ProvisionTeamFolderAsync(
        Guid teamId,
        string folderName,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("[STUB] Would provision Google Drive folder '{FolderName}' for team {TeamId}", folderName, teamId);

        // Return a stub resource
        var resource = new GoogleResource
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            GoogleId = $"stub-folder-{Guid.NewGuid():N}",
            Name = folderName,
            Url = "https://drive.google.com/stub",
            ProvisionedAt = NodaTime.SystemClock.Instance.GetCurrentInstant(),
            IsActive = true
        };

        return Task.FromResult(resource);
    }

    public Task<GoogleResource> ProvisionUserFolderAsync(
        Guid userId,
        string folderName,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("[STUB] Would provision Google Drive folder '{FolderName}' for user {UserId}", folderName, userId);

        // Return a stub resource
        var resource = new GoogleResource
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            GoogleId = $"stub-folder-{Guid.NewGuid():N}",
            Name = folderName,
            Url = "https://drive.google.com/stub",
            ProvisionedAt = NodaTime.SystemClock.Instance.GetCurrentInstant(),
            IsActive = true
        };

        return Task.FromResult(resource);
    }

    public Task SyncResourcePermissionsAsync(Guid resourceId, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("[STUB] Would sync permissions for resource {ResourceId}", resourceId);
        return Task.CompletedTask;
    }

    public Task SyncAllResourcesAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("[STUB] Would sync all Google resources");
        return Task.CompletedTask;
    }

    public Task<GoogleResource?> GetResourceStatusAsync(Guid resourceId, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("[STUB] Would get status for resource {ResourceId}", resourceId);
        return Task.FromResult<GoogleResource?>(null);
    }

    public Task AddUserToTeamResourcesAsync(Guid teamId, Guid userId, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("[STUB] Would add user {UserId} to team {TeamId} Google resources", userId, teamId);
        return Task.CompletedTask;
    }

    public Task RemoveUserFromTeamResourcesAsync(Guid teamId, Guid userId, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("[STUB] Would remove user {UserId} from team {TeamId} Google resources", userId, teamId);
        return Task.CompletedTask;
    }

    public Task<GoogleResource> ProvisionTeamGroupAsync(
        Guid teamId,
        string groupEmail,
        string groupName,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("[STUB] Would provision Google Group '{GroupEmail}' ({GroupName}) for team {TeamId}", groupEmail, groupName, teamId);

        var resource = new GoogleResource
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            ResourceType = Domain.Enums.GoogleResourceType.Group,
            GoogleId = groupEmail,
            Name = groupName,
            Url = $"https://groups.google.com/a/nobodies.team/g/{groupEmail.Split('@')[0]}",
            ProvisionedAt = NodaTime.SystemClock.Instance.GetCurrentInstant(),
            IsActive = true
        };

        return Task.FromResult(resource);
    }

    public Task AddUserToGroupAsync(Guid groupResourceId, string userEmail, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("[STUB] Would add {UserEmail} to group {GroupResourceId}", userEmail, groupResourceId);
        return Task.CompletedTask;
    }

    public Task RemoveUserFromGroupAsync(Guid groupResourceId, string userEmail, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("[STUB] Would remove {UserEmail} from group {GroupResourceId}", userEmail, groupResourceId);
        return Task.CompletedTask;
    }

    public Task SyncTeamGroupMembersAsync(Guid teamId, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("[STUB] Would sync all members for team {TeamId} Google Group", teamId);
        return Task.CompletedTask;
    }

    public Task RestoreUserToAllTeamsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("[STUB] Would restore user {UserId} to all team Google resources", userId);
        return Task.CompletedTask;
    }
}
