using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Debug.Controllers;

/// <summary>Anonymous on purpose: static design reference markup only.</summary>
[AllowAnonymous]
[Route("ColorPalette")]
internal sealed class ColorPaletteController : Controller
{
    [HttpGet("")]
    public IActionResult Index()
    {
        return View();
    }
}
