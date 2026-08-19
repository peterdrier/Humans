using System.Security.Claims;
using Humans.Base.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.ViewComponents;

/// <summary>
/// Renders the member top-nav links sections contributed through <see cref="ISectionNav"/>,
/// gated per item and ordered by weight.
/// </summary>
public sealed class SectionNavViewComponent(
    IEnumerable<ISectionNav> contributors,
    IAuthorizationService authorization,
    IServiceProvider serviceProvider) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        var items = contributors
            .SelectMany(c => c.Items())
            .OrderBy(i => i.Weight)
            .ThenBy(i => i.Label, StringComparer.Ordinal)
            .ToList();

        var visible = new List<MemberNavItem>(items.Count);
        foreach (var item in items)
        {
            if (await IsVisibleAsync(item))
                visible.Add(item);
        }

        return View(visible);
    }

    private async Task<bool> IsVisibleAsync(MemberNavItem item)
    {
        if (item.Visible is not null && !item.Visible(serviceProvider, (ClaimsPrincipal)User))
            return false;

        if (item.Policy is null)
            return true;

        return (await authorization.AuthorizeAsync(HttpContext.User, null, item.Policy)).Succeeded;
    }
}
