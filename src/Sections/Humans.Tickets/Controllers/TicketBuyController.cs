using System.Globalization;
using Humans.Base.Controllers;
using Humans.Tickets.Domain.Checkout;
using Humans.Tickets.Services.Checkout;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

namespace Humans.Tickets.Controllers;

/// <summary>Buying from a Humans profile: Humans prices the order, the event's backend takes the money.</summary>
[Authorize]
[Route("Tickets/Buy")]
internal sealed class TicketBuyController(
    ITicketCheckoutService checkout,
    IUserService userService,
    IStringLocalizer<TicketsResource> localizer) : HumansControllerBase(userService)
{
    /// <summary>Euros. Larger gifts go through the organisers, and the cap keeps the cents conversion in range.</summary>
    private const decimal MaxDonation = 100_000m;

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var (errorResult, user) = await RequireCurrentUserAsync(ct);
        if (errorResult is not null) return errorResult;

        var pages = await checkout.GetBuyPagesAsync(user.Id, ct);
        return View("~/Views/Tickets/Buy/Index.cshtml", pages);
    }

    [HttpPost("")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Start(
        Guid salesEventId, bool forSelf, string? partnerName, string? partnerEmail, long? supporterTotalMinor, decimal? donation,
        TicketDonationPurpose donationPurpose, CancellationToken ct)
    {
        var (errorResult, user) = await RequireCurrentUserAsync(ct);
        if (errorResult is not null) return errorResult;

        var returnBase = new Uri(Url.Action(nameof(Order), "TicketBuy", new { id = Guid.Empty }, Request.Scheme, Request.Host.Value)!
            .Replace(Guid.Empty.ToString(), string.Empty, StringComparison.Ordinal));
        if (donation > MaxDonation)
        {
            SetError(localizer["Tickets_Buy_DonationTooLarge"].Value);
            return RedirectToAction(nameof(Index));
        }
        var donationMinor = donation is > 0 ? (long)Math.Round(donation.Value * 100m, MidpointRounding.AwayFromZero) : 0;

        var result = await checkout.StartCheckoutAsync(new StartCheckout(
            salesEventId, forSelf, partnerName, partnerEmail, supporterTotalMinor, donationMinor, donationPurpose,
            returnBase, CultureInfo.CurrentUICulture.Name), user.Id, ct);

        if (result.Refusal is { } refusal)
        {
            SetError(localizer[refusal].Value);
            return RedirectToAction(nameof(Index));
        }

        return result.PaymentUrl is { } paymentUrl
            ? Redirect(paymentUrl.ToString())
            : RedirectToAction(nameof(Order), new { id = result.OrderId });
    }

    [HttpGet("Order/{id:guid}")]
    public async Task<IActionResult> Order(Guid id, CancellationToken ct)
    {
        var (errorResult, user) = await RequireCurrentUserAsync(ct);
        if (errorResult is not null) return errorResult;

        var order = await checkout.RefreshOrderAsync(id, ct);
        if (order is null || !CanSee(order, user.Id))
            return NotFound();

        return View("~/Views/Tickets/Buy/Order.cshtml", new TicketBuyOrderViewModel(order, user.Id));
    }

    [HttpPost("Order/{id:guid}/Cancel")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
    {
        var (errorResult, user) = await RequireCurrentUserAsync(ct);
        if (errorResult is not null) return errorResult;

        var result = await checkout.CancelAsync(id, user.Id, ct);
        if (result.Refusal is { } refusal)
        {
            if (string.Equals(refusal, "Tickets_Buy_OrderNotFound", StringComparison.Ordinal))
                return NotFound();
            SetError(localizer[refusal].Value);
        }
        return RedirectToAction(nameof(Order), new { id });
    }

    /// <summary>The payer sees the whole order; a holder sees only their own ticket line.</summary>
    private static bool CanSee(TicketCheckoutOrder order, Guid userId) =>
        order.BuyerUserId == userId || order.Lines.Any(l => l.AttendeeUserId == userId);
}

internal sealed record TicketBuyOrderViewModel(TicketCheckoutOrder Order, Guid ViewerId)
{
    public bool IsBuyer => Order.BuyerUserId == ViewerId;

    public IEnumerable<TicketCheckoutOrderLine> VisibleLines =>
        IsBuyer ? Order.Lines : Order.Lines.Where(l => l.AttendeeUserId == ViewerId);
}
