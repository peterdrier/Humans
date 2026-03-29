using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Web.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.Controllers;

public abstract class HumansCampControllerBase : HumansControllerBase
{
    private readonly ICampService _campService;

    protected HumansCampControllerBase(UserManager<User> userManager, ICampService campService)
        : base(userManager)
    {
        _campService = campService;
    }

    protected Task<Camp?> GetCampBySlugAsync(string slug)
    {
        return _campService.GetCampBySlugAsync(slug);
    }

    protected async Task<(bool IsLead, bool IsCampAdmin)> ResolveCampViewerStateAsync(Camp camp)
    {
        var user = await GetCurrentUserAsync();
        if (user is null)
        {
            return (false, false);
        }

        return (
            await _campService.IsUserCampLeadAsync(user.Id, camp.Id),
            RoleChecks.IsCampAdmin(User));
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

        if (RoleChecks.IsCampAdmin(User) || await _campService.IsUserCampLeadAsync(user.Id, camp.Id))
        {
            return (null, user, camp);
        }

        return (Forbid(), user, camp);
    }
}
