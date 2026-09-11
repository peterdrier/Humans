using System.Security.Claims;
using Humans.Workgroups.Services;
using Microsoft.AspNetCore.Mvc;
using NodaTime;

namespace Humans.Workgroups.ViewComponents;

/// <summary>
/// The member dashboard's "My workgroups" (design §16): the signed-in person's own
/// groups with update-due and open-for-comment badges. Renders nothing for an
/// unauthenticated principal; an empty state links to the register.
/// </summary>
/// <remarks>
/// Internal — invoked by name through the Shell's slot renderer
/// (<c>Component.InvokeAsync</c>), never as a <c>&lt;vc:&gt;</c> tag helper, so Razor's
/// public-only tag-helper discovery does not apply. Public would not compile: the
/// constructor takes the section-internal <c>IWorkgroupService</c> (CS0051).
/// </remarks>
internal sealed class MyWorkgroupsViewComponent(IWorkgroupService workgroups, IClock clock) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        if (!Guid.TryParse(UserClaimsPrincipal.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return Content(string.Empty);

        var mine = await workgroups.GetForMemberAsync(userId);
        return View(new MyWorkgroupsViewModel(mine, clock.GetCurrentInstant()));
    }
}

internal sealed record MyWorkgroupsViewModel(IReadOnlyList<WorkgroupInfo> Mine, Instant Now);
