using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.Controllers;

/// <summary>Anonymous design reference page (palette, controls, typography). Linked from the admin sidebar "Design" group.</summary>
[AllowAnonymous]
[Route("ColorPalette")]
public class ColorPaletteController : Controller
{
    [HttpGet("")]
    public IActionResult Index()
    {
        return View();
    }
}
