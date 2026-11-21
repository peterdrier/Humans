using Humans.Base.Controllers;
using Humans.Settings.Contracts;
using Humans.Tickets.Domain.Checkout;
using Humans.Tickets.Services.Checkout;
using Humans.Tickets.Services.Delivery;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Tickets.Controllers;

/// <summary>The tickets a person holds, with their QR codes, and the code images the ticket email links.</summary>
[Authorize]
[Route("Tickets/Mine")]
internal sealed class TicketWalletController(
    ITicketCheckoutService checkout,
    ISettingsService settings,
    TicketCodeImages codeImages,
    IUserService userService) : HumansControllerBase(userService)
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var (errorResult, user) = await RequireCurrentUserAsync(ct);
        if (errorResult is not null) return errorResult;

        var tickets = await checkout.GetTicketsForHolderAsync(user.Id, ct);
        var eventNames = await EventNamesAsync(ct);
        var rows = tickets
            .Where(t => t.State is TicketCheckoutTicketState.Valid or TicketCheckoutTicketState.Offered)
            .OrderBy(t => t.IssuedAt)
            .Select(t => new WalletTicket(t, eventNames.GetValueOrDefault(t.SalesEventId, string.Empty)))
            .ToList();
        return View("~/Views/Tickets/Mine/Index.cshtml", rows);
    }

    /// <summary>Anonymous: mail clients fetch it without a session. Only a token Humans minted renders.</summary>
    [AllowAnonymous]
    [HttpGet("Code/{token}")]
    [ResponseCache(Duration = 86400, Location = ResponseCacheLocation.Client)]
    public IActionResult Code(string token)
    {
        if (codeImages.BarcodeFor(token) is not { } barcode)
            return NotFound();
        return File(TicketCodeImages.Png(barcode), "image/png");
    }

    private async Task<IReadOnlyDictionary<Guid, string>> EventNamesAsync(CancellationToken ct)
    {
        var events = (await settings.GetAllEventSettingsAsync(ct)).ToDictionary(e => e.Id, e => e.EventName);
        return (await checkout.GetSalesEventsAsync(ct))
            .ToDictionary(s => s.Id, s => events.GetValueOrDefault(s.EventSettingsId, string.Empty));
    }
}

internal sealed record WalletTicket(TicketCheckoutTicket Ticket, string EventName)
{
    public string CodeDataUri => TicketCodeImages.PngDataUri(Ticket.Barcode);
}
