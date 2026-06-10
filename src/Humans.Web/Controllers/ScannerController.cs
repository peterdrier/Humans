using Humans.Application.DTOs;
using Humans.Application.Interfaces.Tickets;
using Humans.Web.Authorization;
using Humans.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.Controllers;

/// <summary>Scanner section — in-browser barcode/QR decoders; no server-side writes. See nobodies-collective/Humans#525.</summary>
[Authorize(Policy = PolicyNames.ScannerAccess)]
[Route("Scanner")]
public class ScannerController(ITicketServiceRead tickets) : Controller
{
    [HttpGet("")]
    public IActionResult Index() => View();

    [HttpGet("Barcode")]
    public IActionResult Barcode() => View();

    [HttpGet("Tickets")]
    public IActionResult Tickets() => View();

    [HttpGet("Tickets/Card")]
    public async Task<IActionResult> Card(string barcode, CancellationToken ct)
    {
        var code = barcode?.Trim() ?? string.Empty;
        if (code.Length == 0)
            return PartialView("_TicketCard", new ScannerTicketCardViewModel(false, null, null, null, null, null));

        // Gate scope: only the current event's tickets are admissible here, so a
        // barcode from a previous event reads as "not found" rather than a valid card.
        var orders = await tickets.GetTicketOrdersAsync(ct);
        var hit = orders
            .Where(o => o.IsCurrentEvent)
            .SelectMany(o => o.Attendees)
            .FirstOrDefault(a => string.Equals(a.Barcode, code, StringComparison.Ordinal));

        if (hit is null)
            return PartialView("_TicketCard", new ScannerTicketCardViewModel(false, code, null, null, null, null));

        var stub = new TicketStubInfo(
            AttendeeName: hit.AttendeeName ?? "",
            AttendeeEmail: hit.AttendeeEmail,
            Status: hit.Status,
            HasPendingTransfer: false,
            PendingTransferRequestId: null,
            EarlyEntryDate: null);

        return PartialView("_TicketCard", new ScannerTicketCardViewModel(
            true, code, stub, hit.TicketTypeName, hit.TransferredToName, hit.TransferredAt));
    }
}
