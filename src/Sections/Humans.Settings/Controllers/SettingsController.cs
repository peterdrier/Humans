using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Settings.Controllers;

/// <summary>
/// The member-facing settings page (peterdrier/Humans#1628): renders whatever tabs sections contribute
/// through <c>ISectionSettings</c>. Lives at <c>/Settings</c>, distinct from
/// <c>/Settings/Admin</c> (the app-wide event settings POST endpoint) — any authenticated member
/// can open it, even with zero contributed tabs.
/// </summary>
[Authorize]
[Route("Settings")]
internal sealed class SettingsController : Controller
{
    [HttpGet("")]
    public IActionResult Index() => View();
}
