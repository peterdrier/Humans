using Humans.Application;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.Users;
using Humans.Web.Authorization.Requirements;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.Controllers;

public abstract class HumansCampControllerBase : HumansControllerBase
{
    private readonly ICampService _campService;
    private readonly IAuthorizationService _authorizationService;

    protected HumansCampControllerBase(
        IUserService userService,
        ICampService campService,
        IAuthorizationService authorizationService)
        : base(userService)
    {
        _campService = campService;
        _authorizationService = authorizationService;
    }

    protected Task<CampLookup?> GetCampBySlugAsync(string slug, CancellationToken cancellationToken = default)
    {
        return _campService.GetCampBySlugAsync(slug, cancellationToken);
    }

    protected async Task<(bool IsLead, bool IsCampAdmin)> ResolveCampViewerStateAsync(Guid campId, UserInfo? user, CancellationToken cancellationToken = default)
    {
        var canManage = (await _authorizationService.AuthorizeAsync(User, campId, CampOperationRequirement.Manage)).Succeeded;
        if (!canManage)
        {
            return (false, false);
        }

        if (user is null)
        {
            return (false, false);
        }

        var isLead = await _campService.IsUserCampLeadAsync(user.Id, campId, cancellationToken);
        var isCampAdmin = Authorization.RoleChecks.IsCampAdmin(User);

        return (isLead, isCampAdmin);
    }

    protected async Task<(IActionResult? ErrorResult, UserInfo User, CampLookup Camp)> ResolveCampManagementAsync(string slug)
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

        var result = await _authorizationService.AuthorizeAsync(User, camp, CampOperationRequirement.Manage);
        if (result.Succeeded)
        {
            return (null, user, camp);
        }

        return (Forbid(), user, camp);
    }
}
