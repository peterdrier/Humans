using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.Controllers;

/// <summary>
/// Anonymous design reference page displaying the full color palette,
/// UI controls, and typography samples. For the designer — no nav link.
/// </summary>
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
