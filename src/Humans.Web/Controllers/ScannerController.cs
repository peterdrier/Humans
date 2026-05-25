using Humans.Web.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.Controllers;

/// <summary>Scanner section — in-browser barcode/QR decoders; no server-side writes. See nobodies-collective/Humans#525.</summary>
[Authorize(Policy = PolicyNames.TicketAdminBoardOrAdmin)]
[Route("Scanner")]
public class ScannerController : Controller
{
    [HttpGet("")]
    public IActionResult Index() => View();

    [HttpGet("Barcode")]
    public IActionResult Barcode() => View();
}
