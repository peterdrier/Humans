using System.Text.RegularExpressions;
using Humans.Application.DTOs;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Application.Services.Teams;

/// <summary>
/// Application-layer service for linking and managing pre-shared Google
/// resources for teams. Owns the <c>google_resources</c> table (design-rules
/// §8). Google API calls are routed through
/// <see cref="ITeamResourceGoogleClient"/> so the <c>Humans.Application</c>
/// project stays framework-free — the real Google-backed implementation and
/// the dev/test stub both live in <c>Humans.Infrastructure</c>.
/// </summary>
public sealed partial class TeamResourceService : ITeamResourceService
{
    private readonly IGoogleResourceRepository _repository;
    private readonly ITeamResourceGoogleClient _googleClient;
    private readonly IGoogleSyncService _googleSyncService;
    private readonly ITeamService _teamService;
    private readonly IRoleAssignmentService _roleAssignmentService;
    private readonly IAuditLogService _auditLogService;
    private readonly TeamResourceManagementOptions _resourceOptions;
    private readonly IClock _clock;
    private readonly ILogger<TeamResourceService> _logger;

    public TeamResourceService(
        IGoogleResourceRepository repository,
        ITeamResourceGoogleClient googleClient,
        IGoogleSyncService googleSyncService,
        ITeamService teamService,
        IRoleAssignmentService roleAssignmentService,
        IAuditLogService auditLogService,
        TeamResourceManagementOptions resourceOptions,
        IClock clock,
        ILogger<TeamResourceService> logger)
    {
        _repository = repository;
        _googleClient = googleClient;
        _googleSyncService = googleSyncService;
        _teamService = teamService;
        _roleAssignmentService = roleAssignmentService;
        _auditLogService = auditLogService;
        _resourceOptions = resourceOptions;
        _clock = clock;
        _logger = logger;
    }

    // ==========================================================================
    // Reads
    // ==========================================================================

    public Task<IReadOnlyList<GoogleResource>> GetTeamResourcesAsync(Guid teamId, CancellationToken ct = default)
        => _repository.GetActiveByTeamIdAsync(teamId, ct);

    public Task<IReadOnlyDictionary<Guid, IReadOnlyList<GoogleResource>>> GetResourcesByTeamIdsAsync(
        IReadOnlyCollection<Guid> teamIds,
        CancellationToken ct = default)
        => _repository.GetActiveByTeamIdsAsync(teamIds, ct);

    public async Task<IReadOnlyDictionary<Guid, TeamResourceSummary>> GetTeamResourceSummariesAsync(
        IReadOnlyCollection<Guid> teamIds,
        CancellationToken ct = default)
    {
        if (teamIds.Count == 0)
        {
            return new Dictionary<Guid, TeamResourceSummary>();
        }

        var rowsByTeam = await _repository.GetActiveByTeamIdsAsync(teamIds, ct);

        var result = new Dictionary<Guid, TeamResourceSummary>(teamIds.Count);
        foreach (var teamId in teamIds)
        {
            var rows = rowsByTeam[teamId];
            if (rows.Count == 0)
            {
                result[teamId] = TeamResourceSummary.Empty;
                continue;
            }
            var hasMailGroup = rows.Any(r => r.ResourceType == GoogleResourceType.Group);
            var driveCount = rows.Count(r => r.ResourceType != GoogleResourceType.Group);
            result[teamId] = new TeamResourceSummary(hasMailGroup, driveCount);
        }
        return result;
    }

    public Task<IReadOnlyDictionary<Guid, int>> GetActiveResourceCountsByTeamAsync(CancellationToken ct = default)
        => _repository.GetActiveResourceCountsByTeamAsync(ct);

    public async Task<IReadOnlyList<UserTeamGoogleResource>> GetUserTeamResourcesAsync(
        Guid userId,
        CancellationToken ct = default)
    {
        // In-memory stitch across TeamMember + GoogleResource per design-rules
        // §2c (sibling services own disjoint tables) and §6 (no cross-service
        // EF joins). TeamService owns TeamMembers/Teams; we own GoogleResources.
        var memberships = await _teamService.GetUserTeamsAsync(userId, ct);
        if (memberships.Count == 0)
        {
            return Array.Empty<UserTeamGoogleResource>();
        }

        var teamIds = memberships
            .Where(m => m.Team is not null)
            .Select(m => m.TeamId)
            .Distinct()
            .ToList();
        if (teamIds.Count == 0)
        {
            return Array.Empty<UserTeamGoogleResource>();
        }

        var resourcesByTeam = await _repository.GetActiveByTeamIdsAsync(teamIds, ct);

        // TeamMember → Team lookup (same-section) so we can surface Name/Slug.
        var teamByMembership = memberships
            .Where(m => m.Team is not null)
            .GroupBy(m => m.TeamId)
            .ToDictionary(g => g.Key, g => g.First().Team);

        var rows = new List<UserTeamGoogleResource>();
        foreach (var (teamId, team) in teamByMembership.OrderBy(kvp => kvp.Value.Name, StringComparer.Ordinal))
        {
            if (!resourcesByTeam.TryGetValue(teamId, out var resources) || resources.Count == 0)
            {
                continue;
            }
            foreach (var r in resources.OrderBy(r => r.Name, StringComparer.Ordinal))
            {
                rows.Add(new UserTeamGoogleResource(team.Name, team.Slug, r.Name, r.ResourceType, r.Url));
            }
        }
        return rows;
    }

    public Task<IReadOnlyList<GoogleResource>> GetActiveDriveFoldersAsync(CancellationToken ct = default)
        => _repository.GetActiveDriveFoldersAsync(ct);

    public Task<int> GetResourceCountAsync(CancellationToken ct = default)
        => _repository.GetCountAsync(ct);

    public Task<GoogleResource?> GetResourceByIdAsync(Guid resourceId, CancellationToken ct = default)
        => _repository.GetByIdAsync(resourceId, ct);

    // ==========================================================================
    // Link — Drive folder
    // ==========================================================================

    public async Task<LinkResourceResult> LinkDriveFolderAsync(
        Guid teamId,
        string folderUrl,
        DrivePermissionLevel permissionLevel = DrivePermissionLevel.Contributor,
        CancellationToken ct = default)
    {
        var folderId = ParseDriveFolderId(folderUrl);
        if (folderId is null)
        {
            return new LinkResourceResult(false,
                ErrorMessage: "Invalid Google Drive folder URL. Please use a URL like https://drive.google.com/drive/folders/...");
        }

        var duplicate = await _repository.FindActiveByGoogleIdAsync(teamId, folderId, GoogleResourceType.DriveFolder, ct);
        if (duplicate is not null)
        {
            return new LinkResourceResult(false,
                ErrorMessage: "This Drive folder is already linked to this team.");
        }

        var lookup = await _googleClient.GetDriveItemAsync(folderId, expectFolder: true, ct);
        if (lookup.Item is null)
        {
            return await BuildDriveLookupErrorAsync(lookup.Error, isFolder: true, folderId, ct);
        }

        if (!lookup.Item.IsFolder)
        {
            return new LinkResourceResult(false,
                ErrorMessage: "The provided URL does not point to a Google Drive folder.");
        }

        var now = _clock.GetCurrentInstant();
        var inactive = await _repository.FindInactiveByGoogleIdAsync(teamId, folderId, GoogleResourceType.DriveFolder, ct);
        GoogleResource resource;
        if (inactive is not null)
        {
            var reactivated = await _repository.ReactivateAsync(
                inactive.Id,
                name: lookup.Item.FullPath,
                url: lookup.Item.WebViewLink,
                lastSyncedAt: now,
                newGoogleId: null,
                newPermissionLevel: permissionLevel,
                ct);
            if (reactivated is null)
            {
                _logger.LogWarning(
                    "Inactive Drive folder row {ResourceId} disappeared during reactivation; falling back to insert.",
                    inactive.Id);
                resource = BuildDriveFolderResource(teamId, lookup.Item, permissionLevel, now);
                await _repository.AddAsync(resource, ct);
            }
            else
            {
                resource = reactivated;
            }
        }
        else
        {
            resource = BuildDriveFolderResource(teamId, lookup.Item, permissionLevel, now);
            await _repository.AddAsync(resource, ct);
        }

        _logger.LogInformation("Linked Drive folder {FolderId} ({FolderName}) to team {TeamId} with permission {Permission}",
            lookup.Item.Id, lookup.Item.Name, teamId, permissionLevel);

        return new LinkResourceResult(true, Resource: resource);
    }

    private static GoogleResource BuildDriveFolderResource(
        Guid teamId, DriveItem item, DrivePermissionLevel permissionLevel, Instant now) => new()
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            ResourceType = GoogleResourceType.DriveFolder,
            GoogleId = item.Id,
            Name = item.FullPath,
            Url = item.WebViewLink,
            ProvisionedAt = now,
            LastSyncedAt = now,
            IsActive = true,
            DrivePermissionLevel = permissionLevel,
        };

    // ==========================================================================
    // Link — Drive file
    // ==========================================================================

    public async Task<LinkResourceResult> LinkDriveFileAsync(
        Guid teamId,
        string fileUrl,
        DrivePermissionLevel permissionLevel = DrivePermissionLevel.Contributor,
        CancellationToken ct = default)
    {
        var fileId = ParseDriveFileId(fileUrl);
        if (fileId is null)
        {
            return new LinkResourceResult(false,
                ErrorMessage: "Invalid Google Drive file URL. Please use a URL like https://docs.google.com/spreadsheets/d/... or https://drive.google.com/file/d/...");
        }

        var duplicate = await _repository.FindActiveByGoogleIdAsync(teamId, fileId, GoogleResourceType.DriveFile, ct);
        if (duplicate is not null)
        {
            return new LinkResourceResult(false,
                ErrorMessage: "This Drive file is already linked to this team.");
        }

        var lookup = await _googleClient.GetDriveItemAsync(fileId, expectFolder: false, ct);
        if (lookup.Item is null)
        {
            return await BuildDriveLookupErrorAsync(lookup.Error, isFolder: false, fileId, ct);
        }

        if (lookup.Item.IsFolder)
        {
            return new LinkResourceResult(false,
                ErrorMessage: "The provided URL points to a folder, not a file. Please use the 'Link Drive Resource' form instead.");
        }

        var now = _clock.GetCurrentInstant();
        var inactive = await _repository.FindInactiveByGoogleIdAsync(teamId, fileId, GoogleResourceType.DriveFile, ct);
        GoogleResource resource;
        if (inactive is not null)
        {
            var reactivated = await _repository.ReactivateAsync(
                inactive.Id,
                name: lookup.Item.FullPath,
                url: lookup.Item.WebViewLink,
                lastSyncedAt: now,
                newGoogleId: null,
                newPermissionLevel: permissionLevel,
                ct);
            if (reactivated is null)
            {
                _logger.LogWarning(
                    "Inactive Drive file row {ResourceId} disappeared during reactivation; falling back to insert.",
                    inactive.Id);
                resource = BuildDriveFileResource(teamId, lookup.Item, permissionLevel, now);
                await _repository.AddAsync(resource, ct);
            }
            else
            {
                resource = reactivated;
            }
        }
        else
        {
            resource = BuildDriveFileResource(teamId, lookup.Item, permissionLevel, now);
            await _repository.AddAsync(resource, ct);
        }

        _logger.LogInformation("Linked Drive file {FileId} ({FileName}) to team {TeamId} with permission {Permission}",
            lookup.Item.Id, lookup.Item.Name, teamId, permissionLevel);

        return new LinkResourceResult(true, Resource: resource);
    }

    private static GoogleResource BuildDriveFileResource(
        Guid teamId, DriveItem item, DrivePermissionLevel permissionLevel, Instant now) => new()
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            ResourceType = GoogleResourceType.DriveFile,
            GoogleId = item.Id,
            Name = item.FullPath,
            Url = item.WebViewLink,
            ProvisionedAt = now,
            LastSyncedAt = now,
            IsActive = true,
            DrivePermissionLevel = permissionLevel,
        };

    public async Task<LinkResourceResult> LinkDriveResourceAsync(
        Guid teamId,
        string url,
        DrivePermissionLevel permissionLevel = DrivePermissionLevel.Contributor,
        CancellationToken ct = default)
    {
        if (ParseDriveFolderId(url) is not null)
        {
            return await LinkDriveFolderAsync(teamId, url, permissionLevel, ct);
        }

        if (ParseDriveFileId(url) is not null)
        {
            return await LinkDriveFileAsync(teamId, url, permissionLevel, ct);
        }

        return new LinkResourceResult(false,
            ErrorMessage: "Invalid Google Drive URL. Please use a folder URL (https://drive.google.com/drive/folders/...) or a file URL (https://docs.google.com/spreadsheets/d/...).");
    }

    // ==========================================================================
    // Link — Google Group
    // ==========================================================================

    public async Task<LinkResourceResult> LinkGroupAsync(Guid teamId, string groupEmail, CancellationToken ct = default)
    {
        var normalizedGroupEmail = NormalizeGroupEmail(groupEmail);
        if (normalizedGroupEmail is null)
        {
            return new LinkResourceResult(false,
                ErrorMessage: "Please enter a valid group email address.");
        }

        var duplicate = await _repository.FindActiveGroupByEmailAsync(teamId, normalizedGroupEmail, ct);
        if (duplicate is not null)
        {
            return new LinkResourceResult(false,
                ErrorMessage: "This Google Group is already linked to this team.");
        }

        var lookup = await _googleClient.LookupGroupAsync(normalizedGroupEmail, ct);
        if (lookup.Group is null)
        {
            var err = lookup.Error;
            var hint = err?.StatusCode switch
            {
                404 => "The Google Group was not found or the service account does not have access. Please add the service account as a Group Manager.",
                403 => "The service account does not have permission to access this group. Please add the service account as a Group Manager.",
                _ => $"Google API error ({err?.StatusCode}): {err?.RawMessage}",
            };
            var serviceAccountEmail = await _googleClient.GetServiceAccountEmailAsync(ct);
            return new LinkResourceResult(false, ErrorMessage: hint, ServiceAccountEmail: serviceAccountEmail);
        }

        var now = _clock.GetCurrentInstant();
        var url = lookup.Group.DisplayUrl;

        var inactive = await _repository.FindInactiveGroupByCandidatesAsync(
            teamId,
            lookup.Group.NumericId,
            lookup.Group.NormalizedEmail,
            ct);

        GoogleResource resource;
        if (inactive is not null)
        {
            var reactivated = await _repository.ReactivateAsync(
                inactive.Id,
                name: lookup.Group.NormalizedEmail,
                url: url,
                lastSyncedAt: now,
                newGoogleId: lookup.Group.NumericId,
                newPermissionLevel: null,
                ct);
            if (reactivated is null)
            {
                _logger.LogWarning(
                    "Inactive Group row {ResourceId} disappeared during reactivation; falling back to insert.",
                    inactive.Id);
                resource = BuildGroupResource(teamId, lookup.Group, url, now);
                await _repository.AddAsync(resource, ct);
            }
            else
            {
                resource = reactivated;
            }
        }
        else
        {
            resource = BuildGroupResource(teamId, lookup.Group, url, now);
            await _repository.AddAsync(resource, ct);
        }

        _logger.LogInformation("Linked Google Group {GroupEmail} ({GroupName}) to team {TeamId}",
            lookup.Group.NormalizedEmail, lookup.Group.DisplayName, teamId);

        return new LinkResourceResult(true, Resource: resource);
    }

    private static GoogleResource BuildGroupResource(
        Guid teamId, ResolvedGroup group, string url, Instant now) => new()
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            ResourceType = GoogleResourceType.Group,
            GoogleId = group.NumericId,
            Name = group.NormalizedEmail,
            Url = url,
            ProvisionedAt = now,
            LastSyncedAt = now,
            IsActive = true,
        };

    // ==========================================================================
    // Unlink / Deactivate
    // ==========================================================================

    public async Task UnlinkResourceAsync(Guid resourceId, CancellationToken ct = default)
    {
        var existing = await _repository.GetByIdAsync(resourceId, ct);
        if (existing is null)
        {
            return;
        }

        await _repository.UnlinkAsync(resourceId, ct);
        _logger.LogInformation("Unlinked resource {ResourceId} ({ResourceName})", resourceId, existing.Name);
    }

    public async Task DeactivateResourcesForTeamAsync(
        Guid teamId,
        GoogleResourceType? resourceType = null,
        CancellationToken ct = default)
    {
        var deactivated = await _repository.DeactivateByTeamAsync(teamId, resourceType, ct);
        if (deactivated.Count == 0)
        {
            return;
        }

        foreach (var resource in deactivated)
        {
            await _auditLogService.LogAsync(
                AuditAction.GoogleResourceDeactivated,
                nameof(GoogleResource),
                resource.Id,
                $"Resource '{resource.Name}' deactivated because owning team was soft-deleted.",
                nameof(TeamResourceService));
        }

        _logger.LogInformation(
            "Deactivated {Count} Google resources (type={ResourceType}) for soft-deleted team {TeamId}",
            deactivated.Count, resourceType?.ToString() ?? "all", teamId);
    }

    // ==========================================================================
    // Permission changes
    // ==========================================================================

    public async Task UpdatePermissionLevelAsync(Guid resourceId, DrivePermissionLevel level, CancellationToken ct = default)
    {
        var updated = await _repository.UpdatePermissionLevelAsync(resourceId, level, ct);
        if (updated)
        {
            _logger.LogInformation("Updated DrivePermissionLevel to {Level} for resource {ResourceId}", level, resourceId);
        }
    }

    public async Task SetRestrictInheritedAccessAsync(Guid resourceId, bool restrict, CancellationToken ct = default)
    {
        var existing = await _repository.GetByIdAsync(resourceId, ct);
        if (existing is null)
        {
            return;
        }

        if (existing.ResourceType != GoogleResourceType.DriveFolder)
        {
            _logger.LogWarning("Cannot set RestrictInheritedAccess on non-folder resource {ResourceId} (type: {Type})",
                resourceId, existing.ResourceType);
            return;
        }

        var mutated = await _repository.SetRestrictInheritedAccessAsync(resourceId, restrict, ct);
        if (mutated is null)
        {
            // Row disappeared between the read and the write; nothing to enforce.
            return;
        }

        try
        {
            await _googleSyncService.SetInheritedPermissionsDisabledAsync(mutated.GoogleId, restrict, ct);
            _logger.LogInformation("Set RestrictInheritedAccess={Restrict} for resource {ResourceId} ({GoogleId})",
                restrict, resourceId, mutated.GoogleId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set inheritedPermissionsDisabled on Google Drive for resource {ResourceId} ({GoogleId})",
                resourceId, mutated.GoogleId);
            throw;
        }
    }

    // ==========================================================================
    // Authorization / sharing instructions
    // ==========================================================================

    public async Task<bool> CanManageTeamResourcesAsync(Guid teamId, Guid userId, CancellationToken ct = default)
    {
        if (await _roleAssignmentService.IsUserBoardMemberAsync(userId, ct))
        {
            return true;
        }

        if (await _roleAssignmentService.IsUserTeamsAdminAsync(userId, ct))
        {
            return true;
        }

        if (_resourceOptions.AllowCoordinatorsToManageResources)
        {
            return await _teamService.IsUserCoordinatorOfTeamAsync(teamId, userId, ct);
        }

        return false;
    }

    public Task<string> GetServiceAccountEmailAsync(CancellationToken ct = default)
        => _googleClient.GetServiceAccountEmailAsync(ct);

    // ==========================================================================
    // Internal helpers — URL / email parsing and Google error mapping
    // ==========================================================================

    private async Task<LinkResourceResult> BuildDriveLookupErrorAsync(
        GoogleClientError? error,
        bool isFolder,
        string itemId,
        CancellationToken ct)
    {
        var subject = isFolder ? "folder" : "file";
        var hint = error?.StatusCode switch
        {
            404 => $"The {subject} was not found or the service account does not have access.",
            403 => $"The service account does not have permission to access this {subject}.",
            _ => $"Google API error ({error?.StatusCode}): {error?.RawMessage}",
        };
        _ = itemId; // Already captured in connector logs.

        var serviceAccountEmail = await _googleClient.GetServiceAccountEmailAsync(ct);
        var trailer = isFolder
            ? "Please share the folder with the service account as Contributor."
            : "The file must be on a Shared Drive accessible to the service account.";
        return new LinkResourceResult(false,
            ErrorMessage: $"{hint} {trailer}",
            ServiceAccountEmail: serviceAccountEmail);
    }

    /// <summary>
    /// Parses a Google Drive folder ID from various URL formats. Shared with
    /// tests and the <see cref="LinkDriveResourceAsync"/> dispatcher.
    /// </summary>
    internal static string? ParseDriveFolderId(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        input = input.Trim();

        // Direct folder ID (no URL, just the ID itself)
        if (FolderIdPattern().IsMatch(input))
        {
            return input;
        }

        // https://drive.google.com/drive/folders/{id}
        // https://drive.google.com/drive/u/0/folders/{id}
        // https://drive.google.com/drive/folders/{id}?usp=sharing
        var match = DriveFolderUrlPattern().Match(input);
        if (match.Success)
        {
            return match.Groups["id"].Value;
        }

        // https://drive.google.com/open?id={id}
        match = DriveOpenUrlPattern().Match(input);
        if (match.Success)
        {
            return match.Groups["id"].Value;
        }

        return null;
    }

    /// <summary>
    /// Parses a Google Drive file ID from various URL formats. Supports Google
    /// Docs, Sheets, Slides, and generic Drive file URLs.
    /// </summary>
    internal static string? ParseDriveFileId(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        input = input.Trim();

        // Direct file ID (no URL, just the ID itself)
        if (FileIdPattern().IsMatch(input))
        {
            return input;
        }

        // https://drive.google.com/file/d/{id}/...
        var match = DriveFileUrlPattern().Match(input);
        if (match.Success)
        {
            return match.Groups["id"].Value;
        }

        // https://docs.google.com/spreadsheets/d/{id}/...
        match = GoogleDocsUrlPattern().Match(input);
        if (match.Success)
        {
            return match.Groups["id"].Value;
        }

        // https://drive.google.com/open?id={id}
        match = DriveOpenUrlPattern().Match(input);
        if (match.Success)
        {
            return match.Groups["id"].Value;
        }

        return null;
    }

    internal static string? NormalizeGroupEmail(string? groupEmail)
    {
        if (string.IsNullOrWhiteSpace(groupEmail) || !groupEmail.Contains("@", StringComparison.Ordinal))
        {
            return null;
        }

        return groupEmail.Trim();
    }

    [GeneratedRegex(@"^[a-zA-Z0-9_-]{10,}$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex FolderIdPattern();

    [GeneratedRegex(@"^[a-zA-Z0-9_-]{10,}$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex FileIdPattern();

    [GeneratedRegex(@"drive\.google\.com/(?:drive/)?(?:u/\d+/)?folders/(?<id>[a-zA-Z0-9_-]+)", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex DriveFolderUrlPattern();

    [GeneratedRegex(@"drive\.google\.com/file/d/(?<id>[a-zA-Z0-9_-]+)", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex DriveFileUrlPattern();

    [GeneratedRegex(@"docs\.google\.com/(?:spreadsheets|document|presentation|forms)/d/(?<id>[a-zA-Z0-9_-]+)", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex GoogleDocsUrlPattern();

    [GeneratedRegex(@"drive\.google\.com/open\?id=(?<id>[a-zA-Z0-9_-]+)", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex DriveOpenUrlPattern();
}

/// <summary>
/// Application-layer options for <see cref="TeamResourceService"/> behavior.
/// Bound by the Web layer from configuration.
/// </summary>
public sealed class TeamResourceManagementOptions
{
    /// <summary>
    /// Configuration section name (matches the pre-migration
    /// <c>TeamResourceManagementSettings</c> section).
    /// </summary>
    public const string SectionName = "TeamResourceManagement";

    /// <summary>
    /// When true, team coordinators (non-board, non-admin) can link and unlink
    /// Google resources for their team.
    /// </summary>
    public bool AllowCoordinatorsToManageResources { get; set; }
}
