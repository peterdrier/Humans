using Humans.Base.Authorization;
using Humans.Base.Enums;
using Humans.Grants.Authorization;
using Humans.Grants.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Grants.ViewComponents;

/// <summary>
/// The "Grants" card, contributed to <c>UserPartSlots.AdminDetail</c>. Admin audience only:
/// renders for <see cref="ProfileCardViewMode.Admin"/> when the viewer passes the same
/// <see cref="PolicyNames.HumanAdminBoardOrAdmin"/> policy as the host page, and nothing for
/// anyone else. Shows every grant of the person, whatever its state. A program name links to its
/// admin page only when the viewer passes that page's own checks (<see cref="GrantsPolicies.Manage"/>
/// and <see cref="ProgramOperationRequirement.Manage"/> on the program); otherwise it is plain text.
/// </summary>
internal sealed class UserGrantsViewComponent(
    IGrantManagement grants,
    IAuthorizationService authorization) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(Guid userId, ProfileCardViewMode viewMode)
    {
        if (viewMode != ProfileCardViewMode.Admin
            || !(await authorization.AuthorizeAsync(UserClaimsPrincipal, PolicyNames.HumanAdminBoardOrAdmin)).Succeeded)
            return Content(string.Empty);

        var all = await grants.GetGrantsForUserAsync(userId, HttpContext.RequestAborted);
        if (all.Count == 0) return Content(string.Empty);

        return View(new GrantsCardViewModel(all.OrderByDescending(g => g.IssuedAt).ToList(), grants.Today, await LinkableProgramsAsync(all)));
    }

    private async Task<IReadOnlySet<Guid>> LinkableProgramsAsync(IReadOnlyList<GrantInfo> all)
    {
        var linkable = new HashSet<Guid>();
        if (!(await authorization.AuthorizeAsync(UserClaimsPrincipal, GrantsPolicies.Manage)).Succeeded) return linkable;

        var shown = all.Select(g => g.ProgramId).ToHashSet();
        foreach (var program in (await grants.GetProgramsAsync(HttpContext.RequestAborted)).Where(p => shown.Contains(p.Id)))
        {
            if ((await authorization.AuthorizeAsync(UserClaimsPrincipal, program, ProgramOperationRequirement.Manage)).Succeeded)
                linkable.Add(program.Id);
        }
        return linkable;
    }
}

/// <summary>
/// The grants a card shows, the date their state is read against, and the programs whose admin
/// page the viewer may open.
/// </summary>
internal sealed record GrantsCardViewModel(IReadOnlyList<GrantInfo> Grants, NodaTime.LocalDate Today, IReadOnlySet<Guid> LinkablePrograms);
