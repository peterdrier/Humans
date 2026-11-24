using System.Globalization;
using Humans.Auth.Contracts;
using Humans.Base.Attributes;
using Humans.Base.Controllers;
using Humans.EarlyEntry.Contracts;
using Humans.Tickets.Domain.Checkout;
using Humans.Tickets.Services.Checkout;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

namespace Humans.Tickets.Controllers;

/// <summary>Buying from a Humans profile: Humans prices the order, the event's ticketing platform takes the money.</summary>
[Authorize]
[Route("Tickets/Buy")]
internal sealed class TicketBuyController(
    ITicketCheckoutService checkout,
    ISignInLinkService signInLinks,
    IEarlyEntryService earlyEntry,
    IUserService userService,
    IStringLocalizer<TicketsResource> localizer,
    ILogger<TicketBuyController> logger) : HumansControllerBase(userService)
{
    /// <summary>Euros. Larger gifts go through the organisers, and the cap keeps the cents conversion in range.</summary>
    private const decimal MaxDonation = 100_000m;

    [HttpGet("")]
    [AllowAnonymous]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        if (User.Identity?.IsAuthenticated != true)
            return RedirectToAction(nameof(Public));
        var (errorResult, user) = await RequireCurrentUserAsync(ct);
        if (errorResult is not null) return errorResult;

        var pages = (await checkout.GetBuyPagesAsync(user.Id, ct))
            .OrderBy(page => page.GateOpeningDate).Select(ForDisplay).ToList();
        return View("~/Views/Tickets/Buy/Index.cshtml", pages);
    }

    /// <summary>
    /// The signed-out way in: what is on sale at the public price, and a sign-in or sign-up link by email that lands
    /// on the buy page. Tickets are named and bought from an account, so the account comes first.
    /// </summary>
    [HttpGet("Public")]
    [AllowAnonymous]
    public async Task<IActionResult> Public(CancellationToken ct)
    {
        if (User.Identity?.IsAuthenticated == true)
            return RedirectToAction(nameof(Index));
        var sales = (await checkout.GetPublicSalesAsync(ct)).OrderBy(sale => sale.GateOpeningDate).ToList();
        return View("~/Views/Tickets/Buy/Public.cshtml", sales);
    }

    [HttpPost("Public")]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SendLink(string? email, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(email))
        {
            try
            {
                await signInLinks.SendSignInLinkAsync(email.Trim(), Url.Action(nameof(Index)), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Same answer either way: the page never says whether an address has an account.
                logger.LogError(ex, "Ticket buy link could not be sent");
            }
            SetSuccess(localizer["Tickets_Public_Sent"].Value);
        }
        return RedirectToAction(nameof(Public));
    }

    /// <summary>An invited buyer's directed-resale link: the same buy page, holding that one ticket.</summary>
    [HttpGet("Resale/{token}")]
    public async Task<IActionResult> Resale(string token, CancellationToken ct)
    {
        var (errorResult, user) = await RequireCurrentUserAsync(ct);
        if (errorResult is not null) return errorResult;

        var page = await checkout.GetDirectedBuyPageAsync(token, user.Id, ct);
        if (page is null)
            return NotFound();
        ViewData["DirectedToken"] = token;
        return View("~/Views/Tickets/Buy/Index.cshtml", new[] { ForDisplay(page) });
    }

    private static BuyPage ForDisplay(BuyPage page) =>
        page with { Orders = page.Orders.OrderByDescending(order => order.CreatedAt).ToList() };

    [HttpPost("")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Start(
        Guid settingsId, bool forSelf, string?[]? otherEmails, long? vipTotalMinor, decimal? donation,
        string? donationPurpose, string?[]? childNames, string?[]? childTypes, bool acceptTerms, string? termsFolder, string? termsVersion,
        bool marketingOptIn, string? directedToken, CancellationToken ct)
    {
        var (errorResult, user) = await RequireCurrentUserAsync(ct);
        if (errorResult is not null) return errorResult;

        IActionResult Back(string key, string? argument = null)
        {
            SetError(argument is null ? localizer[key].Value : localizer[key, argument].Value);
            return string.IsNullOrEmpty(directedToken)
                ? RedirectToAction(nameof(Index))
                : RedirectToAction(nameof(Resale), new { token = directedToken });
        }

        if (donation > MaxDonation)
            return Back("Tickets_Buy_DonationTooLarge");
        var returnBase = new Uri(Url.Action(nameof(Order), "TicketBuy", new { id = Guid.Empty }, Request.Scheme, Request.Host.Value)!
            .Replace(Guid.Empty.ToString(), string.Empty, StringComparison.Ordinal));
        var donationMinor = donation is > 0 ? (long)Math.Round(donation.Value * 100m, MidpointRounding.AwayFromZero) : 0;

        var result = await checkout.StartCheckoutAsync(new StartCheckout(
            settingsId, forSelf, vipTotalMinor, donationMinor, donationPurpose,
            returnBase, CultureInfo.CurrentUICulture.Name)
        {
            Children = (childNames ?? []).Select((name, i) => new ChildEntry(name, childTypes?.ElementAtOrDefault(i))).ToList(),
            OtherEmails = otherEmails ?? [],
            AcceptTerms = acceptTerms,
            TermsFolder = termsFolder,
            TermsVersion = termsVersion,
            MarketingOptIn = marketingOptIn,
            DirectedToken = string.IsNullOrWhiteSpace(directedToken) ? null : directedToken,
        }, user.Id, CancellationToken.None); // a buyer who leaves mid-request must not strand a half-made order

        if (result.Refusal is { } refusal)
            return Back(refusal, result.RefusalArgument);

        return result.PaymentUrl is { } paymentUrl
            ? Redirect(paymentUrl.ToString())
            : RedirectToAction(nameof(Order), new { id = result.OrderId });
    }

    // Someone bought for may follow the ticket email here before finishing onboarding.
    [HttpGet("Mine")]
    [AllowBeforeActiveMembership]
    public async Task<IActionResult> Mine(CancellationToken ct)
    {
        var (errorResult, user) = await RequireCurrentUserAsync(ct);
        if (errorResult is not null) return errorResult;

        // Children ride on the adult's entry, so the viewer's own Early Entry applies to every ticket listed.
        ViewData["EarlyEntryDate"] = (await earlyEntry.GetForUserAsync(user.Id, ct))?.EarliestEntryDate;
        var held = await checkout.GetHeldTicketsAsync(user.Id, ct);
        var tickets = held.GroupBy(row => row.Ticket.TicketingSettingsId)
            .SelectMany(evt => evt.GroupBy(row => row.Ticket.HolderUserId == user.Id
                    ? row.Ticket.Id : row.Ticket.ParentTicketId ?? row.Ticket.Id)
                .OrderBy(family => family.First(row => row.Ticket.Id == family.Key).Ticket.IssuedAt)
                .SelectMany(family => family.OrderBy(row => row.Ticket.Id != family.Key)))
            .ToList();
        return View("~/Views/Tickets/Buy/Mine.cshtml", tickets);
    }

    [HttpPost("Mine/{ticketId:guid}/Offer")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Offer(Guid ticketId, CancellationToken ct) =>
        OnTicketAsync(userId => checkout.OfferForResaleAsync(ticketId, userId, ct), "Tickets_Resale_Offered", ct);

    [HttpPost("Mine/{ticketId:guid}/Withdraw")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Withdraw(Guid ticketId, CancellationToken ct) =>
        OnTicketAsync(userId => checkout.WithdrawOfferAsync(ticketId, userId, ct), "Tickets_Resale_Withdrawn", ct);

    [HttpPost("Mine/{ticketId:guid}/Invite")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Invite(Guid ticketId, string? inviteeEmail, CancellationToken ct)
    {
        var claimBase = new Uri(Url.Action(nameof(Resale), "TicketBuy", new { token = "x" }, Request.Scheme, Request.Host.Value)!
            .TrimEnd('x'));
        var addresses = (inviteeEmail ?? string.Empty).Split([',', ';', ' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries);
        return OnTicketAsync(userId => checkout.InviteBuyersAsync(
            ticketId, userId, addresses, claimBase, CultureInfo.CurrentUICulture.Name, ct),
            "Tickets_Resale_Invited", ct);
    }

    [HttpPost("Mine/{ticketId:guid}/CancelInvite")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> CancelInvite(Guid ticketId, CancellationToken ct) =>
        OnTicketAsync(userId => checkout.CancelInviteAsync(ticketId, userId, ct), "Tickets_Resale_InviteCancelled", ct);

    [HttpPost("Mine/{ticketId:guid}/Companion")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> AskCompanion(Guid ticketId, string? companionEmail, string? note, CancellationToken ct) =>
        OnTicketAsync(userId => checkout.RequestCompanionAsync(ticketId, userId, companionEmail ?? string.Empty, note ?? string.Empty, ct),
            "Tickets_Companion_Requested", ct);

    [HttpPost("Mine/{ticketId:guid}/Companion/Withdraw")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> WithdrawCompanion(Guid ticketId, CancellationToken ct) =>
        OnTicketAsync(userId => checkout.WithdrawCompanionRequestAsync(ticketId, userId, ct), "Tickets_Companion_Withdrawn", ct);

    private async Task<IActionResult> OnTicketAsync(
        Func<Guid, Task<CheckoutMutationResult>> act, string successKey, CancellationToken ct)
    {
        var (errorResult, user) = await RequireCurrentUserAsync(ct);
        if (errorResult is not null) return errorResult;

        var result = await act(user.Id);
        if (string.Equals(result.Refusal, "Tickets_Resale_NotFound", StringComparison.Ordinal))
            return NotFound();
        if (result.Refusal is { } refusal)
            SetError(localizer[refusal].Value);
        else
            SetSuccess(localizer[successKey].Value);
        return RedirectToAction(nameof(Mine));
    }

    [HttpGet("Order/{id:guid}")]
    public async Task<IActionResult> Order(Guid id, CancellationToken ct)
    {
        var (errorResult, user) = await RequireCurrentUserAsync(ct);
        if (errorResult is not null) return errorResult;

        // Checked before the refresh, so only the order's own people make it call the ticketing platform.
        if (await checkout.GetOrderDetailAsync(id, ct) is not { } detail || !CanSee(detail.Order, user.Id))
            return NotFound();
        var order = await checkout.RefreshOrderAsync(id, ct) ?? detail.Order;

        var tickets = (await checkout.GetTicketsForOrderAsync(order.Id, ct))
            .OrderBy(ticket => ticket.Kind).ThenBy(ticket => ticket.IssuedAt).ToList();
        return View("~/Views/Tickets/Buy/Order.cshtml", new TicketBuyOrderViewModel(order, tickets, user.Id));
    }

    [HttpPost("Order/{id:guid}/Cancel")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(Guid id, bool startAgain, CancellationToken ct)
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
        return startAgain && result.Refusal is null ? RedirectToAction(nameof(Index)) : RedirectToAction(nameof(Order), new { id });
    }

    /// <summary>The buyer sees the whole order; a holder sees only their own ticket line.</summary>
    private static bool CanSee(TicketCheckoutOrder order, Guid userId) =>
        order.BuyerUserId == userId || order.Lines.Any(l => l.HolderUserId == userId);
}

internal sealed record TicketBuyOrderViewModel(TicketCheckoutOrder Order, IReadOnlyList<TicketCheckoutTicket> Tickets, Guid ViewerId)
{
    public bool IsBuyer => Order.BuyerUserId == ViewerId;

    public IEnumerable<TicketCheckoutOrderLine> VisibleLines =>
        IsBuyer ? Order.Lines : Order.Lines.Where(l => l.HolderUserId == ViewerId);

    public IEnumerable<TicketCheckoutTicket> VisibleTickets =>
        IsBuyer ? Tickets : Tickets.Where(t => t.HolderUserId == ViewerId);
}
