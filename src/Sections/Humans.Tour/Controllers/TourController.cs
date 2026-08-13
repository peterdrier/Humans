using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Tour.Controllers;

/// <summary>Tour section — the public "what is Humans" page. Static content, no services.</summary>
[AllowAnonymous]
[Route("Tour")]
internal sealed class TourController : Controller
{
    [HttpGet("")]
    public IActionResult Index()
    {
        return View();
    }
}
