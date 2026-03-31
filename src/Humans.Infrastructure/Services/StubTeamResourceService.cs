using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;
using Humans.Application.DTOs;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Configuration;
using Humans.Infrastructure.Data;

namespace Humans.Infrastructure.Services;

/// <summary>
/// Stub implementation of ITeamResourceService for development without Google credentials.
/// Performs real database operations but simulates Google API validation.
/// </summary>
public class StubTeamResourceService : ITeamResourceService
{
    private readonly HumansDbContext _dbContext;
    private readonly TeamResourceManagementSettings _resourceSettings;
    private readonly ITeamService _teamService;
    private readonly IRoleAssignmentService _roleAssignmentService;
    private readonly IClock _clock;
    private readonly ILogger<StubTeamResourceService> _logger;

    public StubTeamResourceService(
        HumansDbContext dbContext,
        IOptions<TeamResourceManagementSettings> resourceSettings,
        ITeamService teamService,
        IRoleAssignmentService roleAssignmentService,
        IClock clock,
        ILogger<StubTeamResourceService> logger)
    {
        _dbContext = dbContext;
        _resourceSettings = resourceSettings.Value;
        _teamService = teamService;
        _roleAssignmentService = roleAssignmentService;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GoogleResource>> GetTeamResourcesAsync(Guid teamId, CancellationToken ct = default)
    {
        return await TeamResourcePersistence.GetActiveTeamResourcesAsync(_dbContext, teamId, ct);
    }

    /// <inheritdoc />
    public Task<LinkResourceResult> LinkDriveFolderAsync(Guid teamId, string folderUrl, DrivePermissionLevel permissionLevel = DrivePermissionLevel.Contributor, CancellationToken ct = default)
    {
        _logger.LogInformation("[STUB] Would link Drive folder from URL '{FolderUrl}' to team {TeamId}", folderUrl, teamId);

        var folderId = TeamResourceService.ParseDriveFolderId(folderUrl);
        if (folderId is null)
        {
            return Task.FromResult(new LinkResourceResult(false,
                ErrorMessage: "Invalid Google Drive folder URL."));
        }

        var now = _clock.GetCurrentInstant();
        var resource = new GoogleResource
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            ResourceType = GoogleResourceType.DriveFolder,
            GoogleId = folderId,
            Name = folderId,
            Url = folderUrl,
            ProvisionedAt = now,
            LastSyncedAt = now,
            IsActive = true,
            DrivePermissionLevel = permissionLevel
        };

        _dbContext.GoogleResources.Add(resource);
        _dbContext.SaveChanges();

        return Task.FromResult(new LinkResourceResult(true, Resource: resource));
    }

    /// <inheritdoc />
    public Task<LinkResourceResult> LinkDriveFileAsync(Guid teamId, string fileUrl, DrivePermissionLevel permissionLevel = DrivePermissionLevel.Contributor, CancellationToken ct = default)
    {
        _logger.LogInformation("[STUB] Would link Drive file from URL '{FileUrl}' to team {TeamId}", fileUrl, teamId);

        var fileId = TeamResourceService.ParseDriveFileId(fileUrl);
        if (fileId is null)
        {
            return Task.FromResult(new LinkResourceResult(false,
                ErrorMessage: "Invalid Google Drive file URL."));
        }

        var now = _clock.GetCurrentInstant();
        var resource = new GoogleResource
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            ResourceType = GoogleResourceType.DriveFile,
            GoogleId = fileId,
            Name = fileId,
            Url = fileUrl,
            ProvisionedAt = now,
            LastSyncedAt = now,
            IsActive = true,
            DrivePermissionLevel = permissionLevel
        };

        _dbContext.GoogleResources.Add(resource);
        _dbContext.SaveChanges();

        return Task.FromResult(new LinkResourceResult(true, Resource: resource));
    }

    /// <inheritdoc />
    public Task<LinkResourceResult> LinkDriveResourceAsync(Guid teamId, string url, DrivePermissionLevel permissionLevel = DrivePermissionLevel.Contributor, CancellationToken ct = default)
    {
        return TeamResourceInputValidation.LinkDriveResourceAsync(
            teamId,
            url,
            permissionLevel,
            ct,
            LinkDriveFolderAsync,
            LinkDriveFileAsync);
    }

    /// <inheritdoc />
    public Task<LinkResourceResult> LinkGroupAsync(Guid teamId, string groupEmail, CancellationToken ct = default)
    {
        _logger.LogInformation("[STUB] Would link Google Group '{GroupEmail}' to team {TeamId}", groupEmail, teamId);

        var normalizedGroupEmail = TeamResourceInputValidation.NormalizeGroupEmail(groupEmail);
        if (normalizedGroupEmail is null)
        {
            return Task.FromResult(new LinkResourceResult(false,
                ErrorMessage: TeamResourceValidationMessages.InvalidGroupEmail));
        }

        var now = _clock.GetCurrentInstant();
        var resource = new GoogleResource
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            ResourceType = GoogleResourceType.Group,
            GoogleId = normalizedGroupEmail,
            Name = normalizedGroupEmail,
            Url = $"https://groups.google.com/a/nobodies.team/g/{normalizedGroupEmail.Split('@')[0]}",
            ProvisionedAt = now,
            LastSyncedAt = now,
            IsActive = true
        };

        _dbContext.GoogleResources.Add(resource);
        _dbContext.SaveChanges();

        return Task.FromResult(new LinkResourceResult(true, Resource: resource));
    }

    /// <inheritdoc />
    public async Task UnlinkResourceAsync(Guid resourceId, CancellationToken ct = default)
    {
        var resource = await TeamResourcePersistence.DeactivateResourceAsync(_dbContext, resourceId, ct);
        if (resource is null)
        {
            return;
        }

        _logger.LogInformation("[STUB] Unlinked resource {ResourceId}", resourceId);
    }

    /// <inheritdoc />
    public async Task<bool> CanManageTeamResourcesAsync(Guid teamId, Guid userId, CancellationToken ct = default)
    {
        return await TeamResourceAccessRules.CanManageTeamResourcesAsync(
            _teamService,
            _roleAssignmentService,
            _resourceSettings,
            teamId,
            userId,
            ct);
    }

    /// <inheritdoc />
    public Task<string> GetServiceAccountEmailAsync(CancellationToken ct = default)
    {
        return Task.FromResult("stub-service-account@project.iam.gserviceaccount.com");
    }

    /// <inheritdoc />
    public async Task<GoogleResource?> GetResourceByIdAsync(Guid resourceId, CancellationToken ct = default)
    {
        return await TeamResourcePersistence.GetResourceByIdAsync(_dbContext, resourceId, ct);
    }

    /// <inheritdoc />
    public async Task UpdatePermissionLevelAsync(Guid resourceId, DrivePermissionLevel level, CancellationToken ct = default)
    {
        var resource = await _dbContext.GoogleResources.FindAsync([resourceId], ct);
        if (resource is null) return;

        resource.DrivePermissionLevel = level;
        await _dbContext.SaveChangesAsync(ct);
        _logger.LogInformation("[STUB] Updated DrivePermissionLevel to {Level} for resource {ResourceId}", level, resourceId);
    }
}
