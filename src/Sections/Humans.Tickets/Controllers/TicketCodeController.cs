using Humans.Tickets.Services.Delivery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Tickets.Controllers;

/// <summary>The QR image the ticket email loads.</summary>
[Route("Tickets/Code")]
internal sealed class TicketCodeController(TicketCodeImages codeImages) : Controller
{
    /// <summary>Anonymous: mail clients fetch it without a session. Only a token Humans minted renders.</summary>
    [AllowAnonymous]
    [HttpGet("{token}")]
    [ResponseCache(Duration = 86400, Location = ResponseCacheLocation.Client)]
    public IActionResult Code(string token)
    {
        if (codeImages.BarcodeFor(token) is not { } barcode)
            return NotFound();
        return File(TicketCodeImages.Png(barcode), "image/png");
    }
}
