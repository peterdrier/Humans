using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
    private readonly IServiceProvider _serviceProvider;
    private readonly IRoleAssignmentService _roleAssignmentService;
    private readonly IClock _clock;
    private readonly ILogger<StubTeamResourceService> _logger;

    private ITeamService TeamService => _serviceProvider.GetRequiredService<ITeamService>();

    public StubTeamResourceService(
        HumansDbContext dbContext,
        IOptions<TeamResourceManagementSettings> resourceSettings,
        IServiceProvider serviceProvider,
        IRoleAssignmentService roleAssignmentService,
        IClock clock,
        ILogger<StubTeamResourceService> logger)
    {
        _dbContext = dbContext;
        _resourceSettings = resourceSettings.Value;
        _serviceProvider = serviceProvider;
        _roleAssignmentService = roleAssignmentService;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GoogleResource>> GetTeamResourcesAsync(Guid teamId, CancellationToken ct = default)
    {
        return await _dbContext.GoogleResources
            .AsNoTracking()
            .Where(r => r.TeamId == teamId && r.IsActive)
            .OrderBy(r => r.ProvisionedAt)
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<GoogleResource>>> GetResourcesByTeamIdsAsync(
        IReadOnlyCollection<Guid> teamIds,
        CancellationToken ct = default)
    {
        if (teamIds.Count == 0)
        {
            return new Dictionary<Guid, IReadOnlyList<GoogleResource>>();
        }

        var rows = await _dbContext.GoogleResources
            .AsNoTracking()
            .Where(r => teamIds.Contains(r.TeamId) && r.IsActive)
            .OrderBy(r => r.ProvisionedAt)
            .ToListAsync(ct);

        var result = new Dictionary<Guid, IReadOnlyList<GoogleResource>>(teamIds.Count);
        foreach (var teamId in teamIds)
        {
            result[teamId] = Array.Empty<GoogleResource>();
        }
        foreach (var group in rows.GroupBy(r => r.TeamId))
        {
            result[group.Key] = group.ToList();
        }
        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, TeamResourceSummary>> GetTeamResourceSummariesAsync(
        IReadOnlyCollection<Guid> teamIds,
        CancellationToken ct = default)
    {
        if (teamIds.Count == 0)
        {
            return new Dictionary<Guid, TeamResourceSummary>();
        }

        var rows = await _dbContext.GoogleResources
            .AsNoTracking()
            .Where(r => teamIds.Contains(r.TeamId) && r.IsActive)
            .Select(r => new { r.TeamId, r.ResourceType })
            .ToListAsync(ct);

        var result = new Dictionary<Guid, TeamResourceSummary>(teamIds.Count);
        foreach (var teamId in teamIds)
        {
            result[teamId] = TeamResourceSummary.Empty;
        }
        foreach (var group in rows.GroupBy(r => r.TeamId))
        {
            var hasMailGroup = group.Any(r => r.ResourceType == GoogleResourceType.Group);
            var driveCount = group.Count(r => r.ResourceType != GoogleResourceType.Group);
            result[group.Key] = new TeamResourceSummary(hasMailGroup, driveCount);
        }
        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, int>> GetActiveResourceCountsByTeamAsync(CancellationToken ct = default)
    {
        return await _dbContext.GoogleResources
            .AsNoTracking()
            .Where(r => r.IsActive)
            .GroupBy(r => r.TeamId)
            .Select(g => new { TeamId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.TeamId, x => x.Count, ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<UserTeamGoogleResource>> GetUserTeamResourcesAsync(
        Guid userId,
        CancellationToken ct = default)
    {
        return await (
            from tm in _dbContext.TeamMembers.AsNoTracking()
            where tm.UserId == userId && tm.LeftAt == null
            join t in _dbContext.Teams on tm.TeamId equals t.Id
            join r in _dbContext.GoogleResources on t.Id equals r.TeamId
            where r.IsActive
            orderby t.Name, r.Name
            select new UserTeamGoogleResource(t.Name, t.Slug, r.Name, r.ResourceType, r.Url)
        ).ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GoogleResource>> GetActiveDriveFoldersAsync(CancellationToken ct = default)
    {
        return await _dbContext.GoogleResources
            .AsNoTracking()
            .Where(r => r.IsActive && r.ResourceType == GoogleResourceType.DriveFolder)
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task<int> GetResourceCountAsync(CancellationToken ct = default)
    {
        return await _dbContext.GoogleResources.AsNoTracking().CountAsync(ct);
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
        var resource = await _dbContext.GoogleResources
            .FirstOrDefaultAsync(r => r.Id == resourceId, ct);
        if (resource is null)
        {
            return;
        }

        resource.IsActive = false;
        await _dbContext.SaveChangesAsync(ct);
        _logger.LogInformation("[STUB] Unlinked resource {ResourceId}", resourceId);
    }

    /// <inheritdoc />
    public async Task DeactivateResourcesForTeamAsync(
        Guid teamId,
        GoogleResourceType? resourceType = null,
        CancellationToken ct = default)
    {
        var query = _dbContext.GoogleResources
            .Where(r => r.TeamId == teamId && r.IsActive);
        if (resourceType is { } rt)
        {
            query = query.Where(r => r.ResourceType == rt);
        }

        var resources = await query.ToListAsync(ct);

        if (resources.Count == 0)
        {
            return;
        }

        foreach (var resource in resources)
        {
            resource.IsActive = false;
        }

        await _dbContext.SaveChangesAsync(ct);

        var auditLogService = _serviceProvider.GetRequiredService<IAuditLogService>();
        foreach (var resource in resources)
        {
            await auditLogService.LogAsync(
                AuditAction.GoogleResourceDeactivated,
                nameof(GoogleResource),
                resource.Id,
                $"Resource '{resource.Name}' deactivated because owning team was soft-deleted.",
                nameof(StubTeamResourceService));
        }

        _logger.LogInformation(
            "[STUB] Deactivated {Count} Google resources (type={ResourceType}) for soft-deleted team {TeamId}",
            resources.Count, resourceType?.ToString() ?? "all", teamId);
    }

    /// <inheritdoc />
    public async Task<bool> CanManageTeamResourcesAsync(Guid teamId, Guid userId, CancellationToken ct = default)
    {
        return await TeamResourceAccessRules.CanManageTeamResourcesAsync(
            TeamService,
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
        return await _dbContext.GoogleResources
            .AsNoTracking()
            .Include(r => r.Team)
            .FirstOrDefaultAsync(r => r.Id == resourceId, ct);
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

    /// <inheritdoc />
    public async Task SetRestrictInheritedAccessAsync(Guid resourceId, bool restrict, CancellationToken ct = default)
    {
        var resource = await _dbContext.GoogleResources.FindAsync([resourceId], ct);
        if (resource is null) return;

        resource.RestrictInheritedAccess = restrict;
        await _dbContext.SaveChangesAsync(ct);
        _logger.LogInformation("[STUB] Set RestrictInheritedAccess={Restrict} for resource {ResourceId}", restrict, resourceId);
    }
}
