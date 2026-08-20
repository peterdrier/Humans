using System.Security.Claims;
using Humans.Base.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.ViewComponents;

/// <summary>
/// Renders the member top-nav links sections contributed through <see cref="ISectionNav"/>,
/// gated per item and per dropdown child, ordered by weight.
/// </summary>
/// <remarks>
/// Items and children sort by weight alone and the sort is stable, so equal weights keep
/// discovery order — no tie-break, matching <see cref="AdminNavComposition"/>.
/// </remarks>
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
            .ToList();

        var visible = new List<MemberNavItem>(items.Count);
        foreach (var item in items)
        {
            if (!await IsVisibleAsync(item))
                continue;

            if (item.Children is not { Count: > 0 })
            {
                visible.Add(item);
                continue;
            }

            // Children are gated and weighted the same way as their parent; a dropdown whose
            // children are all hidden goes with them rather than rendering as an empty menu.
            var children = new List<MemberNavItem>(item.Children.Count);
            foreach (var child in item.Children.OrderBy(c => c.Weight))
            {
                if (await IsVisibleAsync(child))
                    children.Add(child);
            }

            if (children.Count > 0)
                visible.Add(item with { Children = children });
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
