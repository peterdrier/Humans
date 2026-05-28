using Humans.Application;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.Users;
using Humans.Web.Authorization.Requirements;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.Controllers;

public abstract class HumansCampControllerBase(
    IUserServiceRead userService,
    ICampServiceRead campService,
    IAuthorizationService authorizationService) : HumansControllerBase(userService)
{
    protected ICampServiceRead CampService => campService;

    protected Task<CampInfo?> GetCampBySlugAsync(string slug, CancellationToken cancellationToken = default)
    {
        return campService.GetCampBySlugAsync(slug, cancellationToken);
    }

    protected async Task<(bool IsLead, bool IsCampAdmin)> ResolveCampViewerStateAsync(Guid campId, UserInfo? user, CancellationToken cancellationToken = default)
    {
        var canManage = (await authorizationService.AuthorizeAsync(User, campId, CampOperationRequirement.Manage)).Succeeded;
        if (!canManage)
        {
            return (false, false);
        }

        if (user is null)
        {
            return (false, false);
        }

        var campSettings = await campService.GetSettingsAsync(cancellationToken);
        var camp = (await campService.GetCampsForYearAsync(campSettings.PublicYear, cancellationToken))
            .FirstOrDefault(c => c.Id == campId);
        var isLead = camp?.IsLead(user.Id) == true;
        var isCampAdmin = Authorization.RoleChecks.IsCampAdmin(User);

        return (isLead, isCampAdmin);
    }

    protected async Task<(IActionResult? ErrorResult, UserInfo User, CampInfo Camp)> ResolveCampManagementAsync(string slug)
    {
        var camp = await GetCampBySlugAsync(slug);
        if (camp is null)
        {
            return (NotFound(), null!, null!);
        }

        var (currentUserError, user) = await ResolveCurrentUserOrUnauthorizedAsync();
        if (currentUserError is not null)
        {
            return (currentUserError, null!, camp);
        }

        var result = await authorizationService.AuthorizeAsync(User, camp, CampOperationRequirement.Manage);
        if (result.Succeeded)
        {
            return (null, user, camp);
        }

        return (Forbid(), user, camp);
    }

    /// <summary>
    /// Like <see cref="ResolveCampManagementAsync"/> but authorizes via
    /// <see cref="CampOperationRequirement.SubmitEvent"/> — Lead OR Workshop
    /// (plus CampAdmin / Admin). Used by <c>EventsController</c> so Workshop
    /// Leads can submit camp events on behalf of their camp without inheriting
    /// the broader Camp Lead authority surface.
    /// </summary>
    protected async Task<(IActionResult? ErrorResult, UserInfo User, CampInfo Camp)> ResolveCampEventManagementAsync(string slug)
    {
        var camp = await GetCampBySlugAsync(slug);
        if (camp is null)
        {
            return (NotFound(), null!, null!);
        }

        var (currentUserError, user) = await ResolveCurrentUserOrUnauthorizedAsync();
        if (currentUserError is not null)
        {
            return (currentUserError, null!, camp);
        }

        var result = await authorizationService.AuthorizeAsync(User, camp, CampOperationRequirement.SubmitEvent);
        if (result.Succeeded)
        {
            return (null, user, camp);
        }

        return (Forbid(), user, camp);
    }
}
