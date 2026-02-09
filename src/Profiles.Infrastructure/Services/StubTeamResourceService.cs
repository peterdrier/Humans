using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;
using Profiles.Application.DTOs;
using Profiles.Application.Interfaces;
using Profiles.Domain.Entities;
using Profiles.Domain.Enums;
using Profiles.Infrastructure.Configuration;
using Profiles.Infrastructure.Data;

namespace Profiles.Infrastructure.Services;

/// <summary>
/// Stub implementation of ITeamResourceService for development without Google credentials.
/// Performs real database operations but simulates Google API validation.
/// </summary>
public class StubTeamResourceService : ITeamResourceService
{
    private readonly ProfilesDbContext _dbContext;
    private readonly TeamResourceManagementSettings _resourceSettings;
    private readonly ITeamService _teamService;
    private readonly IClock _clock;
    private readonly ILogger<StubTeamResourceService> _logger;

    public StubTeamResourceService(
        ProfilesDbContext dbContext,
        IOptions<TeamResourceManagementSettings> resourceSettings,
        ITeamService teamService,
        IClock clock,
        ILogger<StubTeamResourceService> logger)
    {
        _dbContext = dbContext;
        _resourceSettings = resourceSettings.Value;
        _teamService = teamService;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GoogleResource>> GetTeamResourcesAsync(Guid teamId, CancellationToken ct = default)
    {
        return await _dbContext.GoogleResources
            .Where(r => r.TeamId == teamId && r.IsActive)
            .OrderBy(r => r.ProvisionedAt)
            .AsNoTracking()
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    public Task<LinkResourceResult> LinkDriveFolderAsync(Guid teamId, string folderUrl, CancellationToken ct = default)
    {
        _logger.LogInformation("[STUB] Would link Drive folder from URL '{FolderUrl}' to team {TeamId}", folderUrl, teamId);

        var folderId = TeamResourceService.ParseDriveFolderId(folderUrl);
        if (folderId == null)
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
            IsActive = true
        };

        _dbContext.GoogleResources.Add(resource);
        _dbContext.SaveChanges();

        return Task.FromResult(new LinkResourceResult(true, Resource: resource));
    }

    /// <inheritdoc />
    public Task<LinkResourceResult> LinkDriveFileAsync(Guid teamId, string fileUrl, CancellationToken ct = default)
    {
        _logger.LogInformation("[STUB] Would link Drive file from URL '{FileUrl}' to team {TeamId}", fileUrl, teamId);

        var fileId = TeamResourceService.ParseDriveFileId(fileUrl);
        if (fileId == null)
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
            IsActive = true
        };

        _dbContext.GoogleResources.Add(resource);
        _dbContext.SaveChanges();

        return Task.FromResult(new LinkResourceResult(true, Resource: resource));
    }

    /// <inheritdoc />
    public Task<LinkResourceResult> LinkDriveResourceAsync(Guid teamId, string url, CancellationToken ct = default)
    {
        // Try folder URL first, then file URL
        var folderId = TeamResourceService.ParseDriveFolderId(url);
        if (folderId != null)
        {
            return LinkDriveFolderAsync(teamId, url, ct);
        }

        var fileId = TeamResourceService.ParseDriveFileId(url);
        if (fileId != null)
        {
            return LinkDriveFileAsync(teamId, url, ct);
        }

        return Task.FromResult(new LinkResourceResult(false,
            ErrorMessage: "Invalid Google Drive URL. Please use a folder URL (https://drive.google.com/drive/folders/...) or a file URL (https://docs.google.com/spreadsheets/d/...)."));
    }

    /// <inheritdoc />
    public Task<LinkResourceResult> LinkGroupAsync(Guid teamId, string groupEmail, CancellationToken ct = default)
    {
        _logger.LogInformation("[STUB] Would link Google Group '{GroupEmail}' to team {TeamId}", groupEmail, teamId);

        if (string.IsNullOrWhiteSpace(groupEmail) || !groupEmail.Contains('@'))
        {
            return Task.FromResult(new LinkResourceResult(false,
                ErrorMessage: "Please enter a valid group email address."));
        }

        var now = _clock.GetCurrentInstant();
        var resource = new GoogleResource
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            ResourceType = GoogleResourceType.Group,
            GoogleId = groupEmail,
            Name = groupEmail,
            Url = $"https://groups.google.com/a/nobodies.team/g/{groupEmail.Split('@')[0]}",
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
        var resource = await _dbContext.GoogleResources
            .FirstOrDefaultAsync(r => r.Id == resourceId, ct);

        if (resource == null)
        {
            return;
        }

        resource.IsActive = false;
        await _dbContext.SaveChangesAsync(ct);

        _logger.LogInformation("[STUB] Unlinked resource {ResourceId}", resourceId);
    }

    /// <inheritdoc />
    public async Task<bool> CanManageTeamResourcesAsync(Guid teamId, Guid userId, CancellationToken ct = default)
    {
        var isBoardMember = await _teamService.IsUserBoardMemberAsync(userId, ct);
        if (isBoardMember)
        {
            return true;
        }

        if (_resourceSettings.AllowMetaleadsToManageResources)
        {
            return await _teamService.IsUserMetaleadOfTeamAsync(teamId, userId, ct);
        }

        return false;
    }

    /// <inheritdoc />
    public Task<string> GetServiceAccountEmailAsync(CancellationToken ct = default)
    {
        return Task.FromResult("stub-service-account@project.iam.gserviceaccount.com");
    }
}
