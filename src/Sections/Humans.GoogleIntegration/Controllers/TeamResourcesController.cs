using Humans.Base.Controllers;
using Humans.GoogleIntegration.Authorization;
using Humans.GoogleIntegration.Contracts;
using Humans.GoogleIntegration.Models;
using Humans.GoogleIntegration.Services;
using Humans.Teams.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Localization;

namespace Humans.GoogleIntegration.Controllers;

/// <summary>A team's linked Google resources, gated per team by <see cref="TeamResourceAuthorizationHandler"/>.</summary>
[Authorize]
[Route("Google/Teams/{slug}/Resources")]
internal sealed class TeamResourcesController(
    IUserService userService,
    ITeamService teamService,
    ITeamResourceManagement teamResources,
    IAuthorizationService authorizationService,
    IGoogleSyncManagement googleSync,
    ILogger<TeamResourcesController> logger,
    IStringLocalizer<GoogleIntegrationResource> localizer) : HumansControllerBase(userService)
{
    [HttpGet("")]
    public async Task<IActionResult> Index(string slug)
    {
        var (error, _, team) = await ResolveManageableTeamAsync(slug, HttpContext.RequestAborted);
        if (error is not null)
        {
            return error;
        }

        var resources = await teamResources.GetTeamResourcesAsync(team.Id, HttpContext.RequestAborted);
        var serviceAccountEmail = await teamResources.GetServiceAccountEmailAsync(HttpContext.RequestAborted);

        var viewModel = new TeamResourcesViewModel
        {
            TeamId = team.Id,
            TeamName = team.Name,
            TeamSlug = team.Slug,
            ServiceAccountEmail = serviceAccountEmail,
            Resources = resources.OrderBy(r => r.ProvisionedAt).Select(r => new GoogleResourceViewModel
            {
                Id = r.Id,
                ResourceType = r.ResourceType switch
                {
                    GoogleResourceType.DriveFolder => "Drive Folder",
                    GoogleResourceType.SharedDrive => "Shared Drive",
                    GoogleResourceType.Group => "Google Group",
                    GoogleResourceType.DriveFile => "Drive File",
                    _ => r.ResourceType.ToString()
                },
                Name = r.Name,
                Url = r.Url,
                GoogleId = r.GoogleId,
                ProvisionedAt = r.ProvisionedAt.ToDateTimeUtc(),
                LastSyncedAt = r.LastSyncedAt?.ToDateTimeUtc(),
                IsActive = r.IsActive,
                ErrorMessage = r.ErrorMessage,
                DrivePermissionLevel = r.DrivePermissionLevel,
                IsDriveResource = r.ResourceType is GoogleResourceType.DriveFolder or GoogleResourceType.DriveFile or GoogleResourceType.SharedDrive,
                RestrictInheritedAccess = r.RestrictInheritedAccess,
                IsDriveFolder = r.ResourceType is GoogleResourceType.DriveFolder
            }).ToList()
        };

        return View(viewModel);
    }

    [Authorize]
    [HttpPost("LinkDrive")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> LinkDriveResource(string slug, LinkDriveResourceModel model)
    {
        var (error, user, team) = await ResolveManageableTeamAsync(slug);
        if (error is not null)
        {
            return error;
        }

        if (!ModelState.IsValid)
        {
            SetError(localizer["GoogleIntegration_TeamResources_InvalidDriveUrl"].Value);
            return RedirectToAction(nameof(Index), new { slug });
        }

        var result = await teamResources.LinkDriveResourceAsync(team.Id, model.ResourceUrl, model.PermissionLevel, actorUserId: user.Id);
        if (result.Success)
            SetSuccess($"Drive resource '{result.Resource!.Name}' linked successfully.");
        else
            SetError(BuildResourceLinkError(result, "Failed to link Drive resource."));

        return RedirectToAction(nameof(Index), new { slug });
    }

    [Authorize]
    [HttpPost("LinkGroup")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> LinkGroup(string slug, LinkGroupModel model)
    {
        var (error, user, team) = await ResolveManageableTeamAsync(slug);
        if (error is not null)
        {
            return error;
        }

        if (!ModelState.IsValid)
        {
            SetError(localizer["GoogleIntegration_TeamResources_InvalidGroupEmail"].Value);
            return RedirectToAction(nameof(Index), new { slug });
        }

        var result = await teamResources.LinkGroupAsync(team.Id, model.GroupEmail, actorUserId: user.Id);
        if (result.Success)
            SetSuccess(string.Format(localizer["GoogleIntegration_TeamResources_GroupLinked"].Value, result.Resource!.Name));
        else
            SetError(BuildResourceLinkError(result, localizer["GoogleIntegration_TeamResources_GroupLinkFailed"].Value));

        return RedirectToAction(nameof(Index), new { slug });
    }

    [Authorize]
    [HttpPost("{resourceId}/PermissionLevel")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdatePermissionLevel(string slug, Guid resourceId, DrivePermissionLevel level)
    {
        var (error, user, team) = await ResolveManageableTeamAsync(slug);
        if (error is not null)
        {
            return error;
        }

        if (!await ResourceMatchesTeamAsync(resourceId, team.Id))
            return NotFound();

        if (level == DrivePermissionLevel.None)
        {
            SetError("Invalid permission level.");
            return RedirectToAction(nameof(Index), new { slug });
        }

        await teamResources.UpdatePermissionLevelAsync(resourceId, level, actorUserId: user.Id);
        SetSuccess($"Permission level updated to {level}.");

        return RedirectToAction(nameof(Index), new { slug });
    }

    [Authorize]
    [HttpPost("{resourceId}/RestrictInheritedAccess")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleRestrictInheritedAccess(string slug, Guid resourceId, [BindRequired] bool restrict)
    {
        var (error, user, team) = await ResolveManageableTeamAsync(slug);
        if (error is not null)
        {
            return error;
        }

        if (!ModelState.IsValid)
        {
            logger.LogWarning("Rejected malformed inherited-access form for resource {ResourceId}", resourceId);
            return BadRequest(ModelState);
        }

        if (!await ResourceMatchesTeamAsync(resourceId, team.Id))
            return NotFound();

        var result = await teamResources.SetRestrictInheritedAccessWithResultAsync(
            resourceId,
            restrict,
            user.Id,
            CancellationToken.None);
        if (result.Succeeded)
        {
            var label = restrict ? "enabled" : "disabled";
            SetSuccess($"Inherited access restriction {label}.");
        }
        else
        {
            SetError(result.ErrorMessage ?? "Failed to update inherited access setting.");
        }

        return RedirectToAction(nameof(Index), new { slug });
    }

    [Authorize]
    [HttpPost("{resourceId}/Unlink")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UnlinkResource(string slug, Guid resourceId)
    {
        var (error, user, team) = await ResolveManageableTeamAsync(slug);
        if (error is not null)
        {
            return error;
        }

        if (!await ResourceMatchesTeamAsync(resourceId, team.Id))
            return NotFound();

        await teamResources.UnlinkResourceAsync(resourceId, actorUserId: user.Id);
        SetSuccess(localizer["GoogleIntegration_TeamResources_ResourceUnlinked"].Value);

        return RedirectToAction(nameof(Index), new { slug });
    }

    [Authorize]
    [HttpPost("{resourceId}/Sync")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SyncResource(string slug, Guid resourceId)
    {
        var (error, _, team) = await ResolveManageableTeamAsync(slug);
        if (error is not null)
        {
            return error;
        }

        if (!await ResourceMatchesTeamAsync(resourceId, team.Id))
            return NotFound();

        try
        {
            // Not RequestAborted: navigating away mid-reconcile would leave the resource half-synced.
            var diff = await googleSync.SyncSingleResourceAsync(
                resourceId,
                SyncAction.Execute,
                CancellationToken.None);
            if (diff.ErrorMessage is not null)
                SetError(string.Format(localizer["GoogleIntegration_TeamResources_ResourceSyncFailed"].Value, diff.ErrorMessage));
            else
                SetSuccess(localizer["GoogleIntegration_TeamResources_ResourceSynced"].Value);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error syncing resource {ResourceId}", resourceId);
            SetError(string.Format(localizer["GoogleIntegration_TeamResources_ResourceSyncFailed"].Value, ex.Message));
        }

        return RedirectToAction(nameof(Index), new { slug });
    }

    /// <summary>Current user, then team by slug (404), then the manage-resources gate (403).</summary>
    private async Task<(IActionResult? Error, UserInfo User, TeamInfo Team)> ResolveManageableTeamAsync(
        string slug, CancellationToken ct = default)
    {
        var (currentUserNotFound, user) = await RequireCurrentUserAsync(ct);
        if (currentUserNotFound is not null)
        {
            return (currentUserNotFound, user, null!);
        }

        // Slug first, then the GUID in the same slot (slug-routes-fallback-to-guid).
        var team = await teamService.GetTeamBySlugAsync(slug, ct);
        if (team is null && Guid.TryParse(slug, out var teamId))
            team = await teamService.GetTeamAsync(teamId, ct);
        if (team is null)
        {
            return (NotFound(), user, null!);
        }

        if (!(await authorizationService.AuthorizeAsync(User, team, TeamResourceManageRequirement.Instance)).Succeeded)
        {
            return (Forbid(), user, team);
        }

        return (null, user, team);
    }

    private async Task<bool> ResourceMatchesTeamAsync(Guid resourceId, Guid teamId)
    {
        var resource = await teamResources.GetResourceByIdAsync(resourceId, HttpContext.RequestAborted);
        if (resource?.TeamId == teamId) return true;
        logger.LogWarning("Rejected resource {ResourceId} outside requested team {TeamId}", resourceId, teamId);
        return false;
    }

    private string BuildResourceLinkError(LinkResourceResult result, string defaultMessage)
    {
        var errorMessage = result.ErrorMessage ?? defaultMessage;
        if (result.ServiceAccountEmail is not null)
        {
            errorMessage += $" {string.Format(localizer["GoogleIntegration_TeamResources_ServiceAccount"].Value, result.ServiceAccountEmail)}";
        }

        return errorMessage;
    }
}
