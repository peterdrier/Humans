using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Web.Authorization.Requirements;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.Controllers;

public abstract class HumansCampControllerBase : HumansControllerBase
{
    private readonly ICampService _campService;
    private readonly IAuthorizationService _authorizationService;

    protected HumansCampControllerBase(
        UserManager<User> userManager,
        ICampService campService,
        IAuthorizationService authorizationService)
        : base(userManager)
    {
        _campService = campService;
        _authorizationService = authorizationService;
    }

    protected Task<Camp?> GetCampBySlugAsync(string slug, CancellationToken cancellationToken = default)
    {
        return _campService.GetCampBySlugAsync(slug, cancellationToken);
    }

    protected async Task<(bool IsLead, bool IsCampAdmin)> ResolveCampViewerStateAsync(Camp camp, CancellationToken cancellationToken = default)
    {
        var canManage = (await _authorizationService.AuthorizeAsync(User, camp, CampOperationRequirement.Manage)).Succeeded;
        if (!canManage)
        {
            return (false, false);
        }

        // Determine whether access is via lead or admin role for view-level distinctions
        var user = await GetCurrentUserAsync();
        if (user is null)
        {
            return (false, false);
        }

        var isLead = await _campService.IsUserCampLeadAsync(user.Id, camp.Id, cancellationToken);
        var isCampAdmin = Authorization.RoleChecks.IsCampAdmin(User);

        return (isLead, isCampAdmin);
    }

    protected async Task<(IActionResult? ErrorResult, User User, Camp Camp)> ResolveCampManagementAsync(string slug)
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
