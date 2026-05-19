using System.Text.RegularExpressions;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Teams;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Application.Services.GoogleIntegration;

/// <summary>
/// Manages pre-shared Google resources for teams; owns the google_resources table (design-rules §8).
/// Google API access through <see cref="ITeamResourceGoogleClient"/>. Section: GoogleIntegration (see memory/architecture/team-resources-google-integration-section.md).
/// </summary>
public sealed partial class TeamResourceService(
    IGoogleResourceRepository repository,
    ITeamResourceGoogleClient googleClient,
    IGoogleDrivePermissionsClient drivePermissions,
    ITeamService teamService,
    IServiceProvider serviceProvider,
    IAuditLogService auditLogService,
    TeamResourceManagementOptions resourceOptions,
    IClock clock,
    ILogger<TeamResourceService> logger) : ITeamResourceService
{
    // Lazy resolution: eagerly injecting IRoleAssignmentService creates a
    // ctor cycle TeamResourceService → IRoleAssignmentService → ISystemTeamSync
    // → IGoogleGroupSync → ITeamResourceService. Only one method uses it.
    private IRoleAssignmentService RoleAssignmentService
        => serviceProvider.GetRequiredService<IRoleAssignmentService>();

    // ==========================================================================
    // Reads
    // ==========================================================================

    public async Task<IReadOnlyList<GoogleResourceSnapshot>> GetTeamResourcesAsync(Guid teamId, CancellationToken ct = default)
    {
        var resources = await repository.GetActiveByTeamIdAsync(teamId, ct);
        return resources.Select(ToSnapshot).ToList();
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<GoogleResourceSnapshot>>> GetResourcesByTeamIdsAsync(
        IReadOnlyCollection<Guid> teamIds,
        CancellationToken ct = default)
    {
        var resourcesByTeam = await repository.GetActiveByTeamIdsAsync(teamIds, ct);
        return resourcesByTeam.ToDictionary(
            kvp => kvp.Key,
            kvp => (IReadOnlyList<GoogleResourceSnapshot>)kvp.Value.Select(ToSnapshot).ToList());
    }

    public async Task<IReadOnlyDictionary<Guid, TeamResourceSummary>> GetTeamResourceSummariesAsync(
        IReadOnlyCollection<Guid> teamIds,
        CancellationToken ct = default)
    {
        if (teamIds.Count == 0)
        {
            return new Dictionary<Guid, TeamResourceSummary>();
        }

        var rowsByTeam = await repository.GetActiveByTeamIdsAsync(teamIds, ct);

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
        => repository.GetActiveResourceCountsByTeamAsync(ct);

    public Task MarkResourceSyncedAsync(Guid resourceId, Instant now, CancellationToken ct = default)
        => repository.MarkSyncedAsync(resourceId, now, ct);

    public Task RecordResourceErrorAsync(Guid resourceId, string errorMessage, CancellationToken ct = default)
        => repository.SetErrorMessageManyAsync([resourceId], errorMessage, ct);

    public async Task<IReadOnlyList<UserTeamGoogleResource>> GetUserTeamResourcesAsync(
        Guid userId,
        CancellationToken ct = default)
    {
        // In-memory stitch across TeamMember + GoogleResource per design-rules
        // §2c (sibling services own disjoint tables) and §6 (no cross-service
        // EF joins). TeamService owns TeamMembers/Teams; we own GoogleResources.
        var memberships = await teamService.GetUserTeamsAsync(userId, ct);
        if (memberships.Count == 0)
        {
            return [];
        }

        var teamIds = memberships
            .Select(m => m.TeamId)
            .Distinct()
            .ToList();
        if (teamIds.Count == 0)
        {
            return [];
        }

        var resourcesByTeam = await repository.GetActiveByTeamIdsAsync(teamIds, ct);

        // TeamMember → Team lookup (same-section) so we can surface Name/Slug.
        var teamByMembership = memberships
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

    public async Task<IReadOnlyList<GoogleResourceSnapshot>> GetActiveDriveFoldersAsync(CancellationToken ct = default)
    {
        var resources = await repository.GetActiveDriveFoldersAsync(ct);
        return resources.Select(ToSnapshot).ToList();
    }

    public Task<int> GetResourceCountAsync(CancellationToken ct = default)
        => repository.GetCountAsync(ct);

    public Task<IReadOnlyDictionary<Guid, string>> GetResourceNamesByIdsAsync(
        IReadOnlyCollection<Guid> resourceIds,
        CancellationToken ct = default)
        => repository.GetNamesByIdsAsync(resourceIds, ct);

    public async Task<GoogleResourceSnapshot?> GetResourceByIdAsync(Guid resourceId, CancellationToken ct = default)
    {
        var resource = await repository.GetByIdAsync(resourceId, ct);
        return resource is null
            ? null
            : new GoogleResourceSnapshot(
                resource.Id,
                resource.TeamId,
                resource.GoogleId,
                resource.Name,
                resource.ResourceType,
                resource.Url,
                resource.ProvisionedAt,
                resource.LastSyncedAt,
                resource.IsActive,
                resource.ErrorMessage,
                resource.DrivePermissionLevel,
                resource.RestrictInheritedAccess);
    }

    private static GoogleResourceSnapshot ToSnapshot(GoogleResource resource) =>
        new(
            resource.Id,
            resource.TeamId,
            resource.GoogleId,
            resource.Name,
            resource.ResourceType,
            resource.Url,
            resource.ProvisionedAt,
            resource.LastSyncedAt,
            resource.IsActive,
            resource.ErrorMessage,
            resource.DrivePermissionLevel,
            resource.RestrictInheritedAccess);

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

        var duplicate = await repository.FindActiveByGoogleIdAsync(teamId, folderId, GoogleResourceType.DriveFolder, ct);
        if (duplicate is not null)
        {
            return new LinkResourceResult(false,
                ErrorMessage: "This Drive folder is already linked to this team.");
        }

        var lookup = await googleClient.GetDriveItemAsync(folderId, expectFolder: true, ct);
        if (lookup.Item is null)
        {
            return await BuildDriveLookupErrorAsync(lookup.Error, isFolder: true, folderId, ct);
        }

        if (!lookup.Item.IsFolder)
        {
            return new LinkResourceResult(false,
                ErrorMessage: "The provided URL does not point to a Google Drive folder.");
        }

        var now = clock.GetCurrentInstant();
        var inactive = await repository.FindInactiveByGoogleIdAsync(teamId, folderId, GoogleResourceType.DriveFolder, ct);
        var resource = await ReactivateOrInsertAsync(
            inactive,
            () => BuildDriveFolderResource(teamId, lookup.Item, permissionLevel, now),
            id => repository.ReactivateAsync(id, lookup.Item.FullPath, lookup.Item.WebViewLink, now, null, permissionLevel, ct),
            "Drive folder",
            ct);

        logger.LogInformation("Linked Drive folder {FolderId} ({FolderName}) to team {TeamId} with permission {Permission}",
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

        var duplicate = await repository.FindActiveByGoogleIdAsync(teamId, fileId, GoogleResourceType.DriveFile, ct);
        if (duplicate is not null)
        {
            return new LinkResourceResult(false,
                ErrorMessage: "This Drive file is already linked to this team.");
        }

        var lookup = await googleClient.GetDriveItemAsync(fileId, expectFolder: false, ct);
        if (lookup.Item is null)
        {
            return await BuildDriveLookupErrorAsync(lookup.Error, isFolder: false, fileId, ct);
        }

        if (lookup.Item.IsFolder)
        {
            return new LinkResourceResult(false,
                ErrorMessage: "The provided URL points to a folder, not a file. Please use the 'Link Drive Resource' form instead.");
        }

        var now = clock.GetCurrentInstant();
        var inactive = await repository.FindInactiveByGoogleIdAsync(teamId, fileId, GoogleResourceType.DriveFile, ct);
        var resource = await ReactivateOrInsertAsync(
            inactive,
            () => BuildDriveFileResource(teamId, lookup.Item, permissionLevel, now),
            id => repository.ReactivateAsync(id, lookup.Item.FullPath, lookup.Item.WebViewLink, now, null, permissionLevel, ct),
            "Drive file",
            ct);

        logger.LogInformation("Linked Drive file {FileId} ({FileName}) to team {TeamId} with permission {Permission}",
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

        var duplicate = await repository.FindActiveGroupByEmailAsync(teamId, normalizedGroupEmail, ct);
        if (duplicate is not null)
        {
            return new LinkResourceResult(false,
                ErrorMessage: "This Google Group is already linked to this team.");
        }

        var lookup = await googleClient.LookupGroupAsync(normalizedGroupEmail, ct);
        if (lookup.Group is null)
        {
            var err = lookup.Error;
            var hint = err?.StatusCode switch
            {
                404 => "The Google Group was not found or the service account does not have access. Please add the service account as a Group Manager.",
                403 => "The service account does not have permission to access this group. Please add the service account as a Group Manager.",
                _ => $"Google API error ({err?.StatusCode}): {err?.RawMessage}",
            };
            var serviceAccountEmail = await googleClient.GetServiceAccountEmailAsync(ct);
            return new LinkResourceResult(false, ErrorMessage: hint, ServiceAccountEmail: serviceAccountEmail);
        }

        var now = clock.GetCurrentInstant();
        var url = lookup.Group.DisplayUrl;

        var inactive = await repository.FindInactiveGroupByCandidatesAsync(
            teamId,
            lookup.Group.NumericId,
            lookup.Group.NormalizedEmail,
            ct);

        var resource = await ReactivateOrInsertAsync(
            inactive,
            () => BuildGroupResource(teamId, lookup.Group, url, now),
            id => repository.ReactivateAsync(id, lookup.Group.NormalizedEmail, url, now, lookup.Group.NumericId, null, ct),
            "Group",
            ct);

        logger.LogInformation("Linked Google Group {GroupEmail} ({GroupName}) to team {TeamId}",
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

    private async Task<GoogleResource> ReactivateOrInsertAsync(
        GoogleResource? inactive,
        Func<GoogleResource> buildFresh,
        Func<Guid, Task<GoogleResource?>> reactivate,
        string resourceTypeLabel,
        CancellationToken ct)
    {
        if (inactive is not null)
        {
            var reactivated = await reactivate(inactive.Id);
            if (reactivated is not null)
                return reactivated;

            logger.LogWarning(
                "Inactive {ResourceTypeLabel} row {ResourceId} disappeared during reactivation; falling back to insert.",
                resourceTypeLabel, inactive.Id);
        }

        var fresh = buildFresh();
        await repository.AddAsync(fresh, ct);
        return fresh;
    }

    // ==========================================================================
    // Unlink / Deactivate
    // ==========================================================================

    public async Task UnlinkResourceAsync(Guid resourceId, CancellationToken ct = default)
    {
        var existing = await repository.GetByIdAsync(resourceId, ct);
        if (existing is null)
        {
            logger.LogWarning("UnlinkResourceAsync: resource {ResourceId} not found", resourceId);
            return;
        }

        await repository.UnlinkAsync(resourceId, ct);
        logger.LogInformation("Unlinked resource {ResourceId} ({ResourceName})", resourceId, existing.Name);
    }

    public async Task DeactivateResourcesForTeamAsync(
        Guid teamId,
        GoogleResourceType? resourceType = null,
        CancellationToken ct = default)
    {
        var deactivated = await repository.DeactivateByTeamAsync(teamId, resourceType, ct);
        if (deactivated.Count == 0)
        {
            return;
        }

        foreach (var resource in deactivated)
        {
            await auditLogService.LogAsync(
                AuditAction.GoogleResourceDeactivated,
                nameof(GoogleResource),
                resource.Id,
                $"Resource '{resource.Name}' deactivated because owning team was soft-deleted.",
                nameof(TeamResourceService));
        }

        logger.LogInformation(
            "Deactivated {Count} Google resources (type={ResourceType}) for soft-deleted team {TeamId}",
            deactivated.Count, resourceType?.ToString() ?? "all", teamId);
    }

    // ==========================================================================
    // Permission changes
    // ==========================================================================

    public async Task UpdatePermissionLevelAsync(Guid resourceId, DrivePermissionLevel level, CancellationToken ct = default)
    {
        var updated = await repository.UpdatePermissionLevelAsync(resourceId, level, ct);
        if (updated)
        {
            logger.LogInformation("Updated DrivePermissionLevel to {Level} for resource {ResourceId}", level, resourceId);
        }
        else
        {
            logger.LogWarning("UpdatePermissionLevelAsync: resource {ResourceId} not found", resourceId);
        }
    }

    public async Task SetRestrictInheritedAccessAsync(Guid resourceId, bool restrict, CancellationToken ct = default)
    {
        var existing = await repository.GetByIdAsync(resourceId, ct);
        if (existing is null)
        {
            logger.LogWarning("SetRestrictInheritedAccessAsync: resource {ResourceId} not found", resourceId);
            return;
        }

        if (existing.ResourceType != GoogleResourceType.DriveFolder)
        {
            logger.LogWarning("Cannot set RestrictInheritedAccess on non-folder resource {ResourceId} (type: {Type})",
                resourceId, existing.ResourceType);
            return;
        }

        var mutated = await repository.SetRestrictInheritedAccessAsync(resourceId, restrict, ct);
        if (mutated is null)
        {
            // Row disappeared between the read and the write; nothing to enforce.
            return;
        }

        try
        {
            var error = await drivePermissions.SetInheritedPermissionsDisabledAsync(mutated.GoogleId, restrict, ct);
            if (error is not null)
            {
                throw new InvalidOperationException(
                    $"Google Drive inheritedPermissionsDisabled update failed for {mutated.GoogleId}: HTTP {error.StatusCode} — {error.RawMessage}");
            }
            logger.LogInformation("Set RestrictInheritedAccess={Restrict} for resource {ResourceId} ({GoogleId})",
                restrict, resourceId, mutated.GoogleId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to set inheritedPermissionsDisabled on Google Drive for resource {ResourceId} ({GoogleId})",
                resourceId, mutated.GoogleId);
            throw;
        }
    }

    public async Task<TeamResourceMutationResult> SetRestrictInheritedAccessWithResultAsync(
        Guid resourceId,
        bool restrict,
        CancellationToken ct = default)
    {
        try
        {
            await SetRestrictInheritedAccessAsync(resourceId, restrict, ct);
            return TeamResourceMutationResult.Success();
        }
        catch (Exception ex)
        {
            return TeamResourceMutationResult.Failed($"Failed to update inherited access setting: {ex.Message}");
        }
    }

    // ==========================================================================
    // Authorization / sharing instructions
    // ==========================================================================

    public async Task<bool> CanManageTeamResourcesAsync(Guid teamId, Guid userId, CancellationToken ct = default)
    {
        if (await RoleAssignmentService.IsUserBoardMemberAsync(userId, ct))
        {
            return true;
        }

        if (await RoleAssignmentService.IsUserTeamsAdminAsync(userId, ct))
        {
            return true;
        }

        if (resourceOptions.AllowCoordinatorsToManageResources)
        {
            return await teamService.IsUserCoordinatorOfTeamAsync(teamId, userId, ct);
        }

        return false;
    }

    public Task<string> GetServiceAccountEmailAsync(CancellationToken ct = default)
        => googleClient.GetServiceAccountEmailAsync(ct);

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

        var serviceAccountEmail = await googleClient.GetServiceAccountEmailAsync(ct);
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
