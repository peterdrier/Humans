using Humans.Base.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Humans.Users.Contracts;

namespace Humans.Camps.Contracts;

public abstract class HumansCampControllerBase(
    IUserService userService,
    ICampService campService,
    IAuthorizationService authorizationService) : HumansControllerBase(userService)
{
    protected ICampService CampService => campService;

    protected Task<CampInfo?> GetCampBySlugAsync(string slug, CancellationToken cancellationToken = default)
    {
        return campService.GetCampBySlugAsync(slug, cancellationToken);
    }

    protected async Task<(bool IsLead, bool IsCampAdmin)> ResolveCampViewerStateAsync(CampInfo camp, UserInfo? user)
    {
        // Authorize against the loaded camp, not its id: the id path re-resolves the camp
        // through the active event's projection, which cannot see a camp whose only season is
        // for a later event — locking its own lead out.
        var canManage = (await authorizationService.AuthorizeAsync(User, camp, CampOperationRequirement.Manage)).Succeeded;
        if (!canManage)
        {
            return (false, false);
        }

        if (user is null)
        {
            return (false, false);
        }

        return (camp.IsLead(user.Id), CampsRoles.IsCampsAdmin(User));
    }

    protected async Task<(IActionResult? ErrorResult, UserInfo User, CampInfo Camp)> ResolveCampManagementAsync(string slug)
    {
        var ct = HttpContext.RequestAborted;
        var camp = await GetCampBySlugAsync(slug, ct);
        if (camp is null)
        {
            return (NotFound(), null!, null!);
        }

        var (currentUserError, user) = await ResolveCurrentUserOrUnauthorizedAsync(ct);
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
    /// Authorizes via <see cref="CampOperationRequirement.SubmitEvent"/>, so Workshop Leads can submit camp events
    /// without inheriting the broader Camp Lead authority.
    /// </summary>
    protected async Task<(IActionResult? ErrorResult, UserInfo User, CampInfo Camp)> ResolveCampEventManagementAsync(string slug)
    {
        var ct = HttpContext.RequestAborted;
        var camp = await GetCampBySlugAsync(slug, ct);
        if (camp is null)
        {
            return (NotFound(), null!, null!);
        }

        var (currentUserError, user) = await ResolveCurrentUserOrUnauthorizedAsync(ct);
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
