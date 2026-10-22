using System.Security.Claims;
using Humans.Workgroups.Services;
using Microsoft.AspNetCore.Mvc;
using NodaTime;

namespace Humans.Workgroups.ViewComponents;

/// <summary>
/// Internal: invoked by Type through the Shell's slot renderer, never as a tag helper. Public wouldn't compile:
/// the constructor takes the internal <c>IWorkgroupService</c>.
/// </summary>
internal sealed class MyWorkgroupsViewComponent(IWorkgroupService workgroups, IClock clock) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        if (!Guid.TryParse(UserClaimsPrincipal.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return Content(string.Empty);

        var mine = await workgroups.GetForMemberAsync(userId, HttpContext.RequestAborted);
        return View(new MyWorkgroupsViewModel(mine, clock.GetCurrentInstant()));
    }
}

internal sealed record MyWorkgroupsViewModel(IReadOnlyList<WorkgroupInfo> Mine, Instant Now);
