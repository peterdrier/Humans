using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Tour.Controllers;

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
