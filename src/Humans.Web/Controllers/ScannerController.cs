using Humans.Web.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.Controllers;

/// <summary>
/// Scanner section — in-browser tools for decoding barcodes / QR codes / similar from the
/// device camera. Phase 1 (issue nobodies-collective/Humans#525) only ships the barcode decoder; later tools live at
/// <c>/Scanner/{ToolName}</c>. Nothing on this section writes server-side state.
/// </summary>
[Authorize(Policy = PolicyNames.TicketAdminBoardOrAdmin)]
[Route("Scanner")]
public class ScannerController : Controller
{
    [HttpGet("")]
    public IActionResult Index() => View();

    [HttpGet("Barcode")]
    public IActionResult Barcode() => View();
}
