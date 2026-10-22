using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Settings.Controllers;

/// <summary>Any signed-in member can open it, even with zero contributed tabs.</summary>
[Authorize]
[Route("Settings")]
internal sealed class SettingsController : Controller
{
    [HttpGet("")]
    public IActionResult Index() => View();
}
