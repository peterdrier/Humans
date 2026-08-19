using Humans.Base.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.ViewComponents;

/// <summary>
/// Renders whatever the active sections contributed into a named chrome slot, so Shell no
/// longer decides which section components appear in its layouts.
/// </summary>
/// <remarks>
/// Both <see cref="ISectionChrome"/> (layout chrome) and <see cref="ISectionMemberDashboard"/>
/// (dashboard content) feed the same slots. An empty slot renders nothing at all.
/// Ordering is by weight and stable, so equal weights keep discovery order — no tie-break,
/// matching <see cref="AdminNavComposition"/> and <see cref="SectionNavViewComponent"/>.
/// </remarks>
public sealed class ChromeSlotViewComponent(
    IEnumerable<ISectionChrome> chrome,
    IEnumerable<ISectionMemberDashboard> dashboard) : ViewComponent
{
    public IViewComponentResult Invoke(string name) =>
        View(chrome.SelectMany(c => c.Components())
            .Concat(dashboard.SelectMany(c => c.Components()))
            .Where(c => string.Equals(c.Slot, name, StringComparison.Ordinal))
            .OrderBy(c => c.Weight)
            .ToList());
}
