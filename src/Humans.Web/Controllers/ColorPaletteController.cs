using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.Controllers;

/// <summary>Anonymous design reference page (palette, controls, typography). No nav link.</summary>
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
