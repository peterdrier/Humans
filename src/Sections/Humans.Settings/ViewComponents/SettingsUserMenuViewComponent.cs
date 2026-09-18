using Microsoft.AspNetCore.Mvc;

namespace Humans.Settings.ViewComponents;

/// <summary>
/// The Settings link in the signed-in user menu. Unconditional for signed-in users, same
/// as the /Settings page's own [Authorize] — no policy or count to compute here.
/// </summary>
internal sealed class SettingsUserMenuViewComponent : ViewComponent
{
    public IViewComponentResult Invoke() => View();
}
