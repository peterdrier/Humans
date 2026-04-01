using Microsoft.Extensions.Logging;
using Humans.Application.DTOs;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Domain.Enums;

namespace Humans.Infrastructure.Services;

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

    public Task<GroupLinkResult> EnsureTeamGroupAsync(Guid teamId, bool confirmReactivation = false, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("[STUB] Would ensure Google Group exists for team {TeamId}", teamId);
        return Task.FromResult(GroupLinkResult.Ok());
    }

    public Task<GroupSettingsDriftResult> CheckGroupSettingsAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("[STUB] Would check Google Group settings for drift");
        return Task.FromResult(new GroupSettingsDriftResult());
    }

    public Task<EmailBackfillResult> GetEmailMismatchesAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("[STUB] Would check email mismatches against Admin SDK");
        return Task.FromResult(new EmailBackfillResult());
    }

    public Task<bool> RemediateGroupSettingsAsync(string groupEmail, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("[STUB] Would remediate settings for Google Group {GroupEmail}", groupEmail);
        return Task.FromResult(true);
    }

    public Task<AllGroupsResult> GetAllDomainGroupsAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("[STUB] Would enumerate all domain groups");
        return Task.FromResult(new AllGroupsResult());
    }

    public Task<int> UpdateDriveFolderPathsAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("[STUB] Would update Drive folder paths");
        return Task.FromResult(0);
    }

    public Task<SyncPreviewResult> SyncResourcesByTypeAsync(
        GoogleResourceType resourceType,
        SyncAction action,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("[STUB] Would sync resources of type {ResourceType} with action {Action}", resourceType, action);
        return Task.FromResult(new SyncPreviewResult());
    }

    public Task<ResourceSyncDiff> SyncSingleResourceAsync(
        Guid resourceId,
        SyncAction action,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("[STUB] Would sync single resource {ResourceId} with action {Action}", resourceId, action);
        return Task.FromResult(new ResourceSyncDiff { ResourceId = resourceId });
    }
}
