using Humans.Users.Contracts;
using Humans.Base.Authorization;
using Humans.Base.Enums;
using Humans.Grants.Authorization;
using Humans.Grants.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Grants.ViewComponents;

/// <summary>
/// Renders only for <see cref="ProfileCardViewMode.Admin"/> to a viewer passing the host page's
/// <see cref="UsersPolicies.Read"/>. A program name links only when the viewer passes that page's own
/// checks; otherwise it is plain text.
/// </summary>
internal sealed class UserGrantsViewComponent(
    IGrantManagement grants,
    IAuthorizationService authorization) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(Guid userId, ProfileCardViewMode viewMode)
    {
        if (viewMode != ProfileCardViewMode.Admin
            || !(await authorization.AuthorizeAsync(UserClaimsPrincipal, UsersPolicies.Read)).Succeeded)
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

internal sealed record GrantsCardViewModel(IReadOnlyList<GrantInfo> Grants, NodaTime.LocalDate Today, IReadOnlySet<Guid> LinkablePrograms);
