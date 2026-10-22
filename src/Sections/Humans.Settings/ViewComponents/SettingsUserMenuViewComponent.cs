using Microsoft.AspNetCore.Mvc;

namespace Humans.Settings.ViewComponents;

internal sealed class SettingsUserMenuViewComponent : ViewComponent
{
    public IViewComponentResult Invoke() => View();
}
