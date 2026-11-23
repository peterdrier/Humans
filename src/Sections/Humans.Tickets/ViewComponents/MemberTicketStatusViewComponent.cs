using Humans.Base.Extensions;
using Humans.Settings.Contracts;
using Humans.Tickets.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Humans.Tickets.ViewComponents;

/// <summary>
/// A failed holdings read says the status is unavailable, never "no ticket" with buy/declare actions. Renders
/// nothing off season unless a sale is open in Humans.
/// </summary>
public sealed class MemberTicketStatusViewComponent(
    ITicketService ticketService,
    ISettingsService settingsService,
    IOptions<TicketVendorSettings> ticketSettings,
    ILogger<MemberTicketStatusViewComponent> logger) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        if (UserClaimsPrincipal.GetUserId() is not { } userId)
        {
            return Content(string.Empty);
        }

        var ct = HttpContext.RequestAborted;
        var activeEvent = settingsService.GetActiveEventSettings();
        // Resolved per call: the checkout service is internal, and a public view component cannot name it in its
        // constructor.
        var checkout = HttpContext.RequestServices.GetRequiredService<Services.Checkout.ITicketCheckoutService>();
        var salesEvents = await checkout.GetSalesEventsAsync(ct);
        var sellsInHumans = salesEvents.Any(e => e.Mode is Domain.Checkout.TicketSalesMode.Open or Domain.Checkout.TicketSalesMode.ResaleOnly)
            || (salesEvents.Any(e => e.Mode == Domain.Checkout.TicketSalesMode.InvitedOnly)
                && (await checkout.GetBuyPagesAsync(userId, ct)).Any(p => p.SalesEvent.Mode == Domain.Checkout.TicketSalesMode.InvitedOnly));
        // An open sale wins over off season: this card is the members' way to the buy page.
        if (activeEvent?.IsOffSeason == true && !sellsInHumans)
        {
            return Content(string.Empty);
        }

        var humansTickets = await HumansTicketCountAsync(checkout, userId, activeEvent, salesEvents, ct);
        var configured = ticketSettings.Value.IsConfigured;
        var ticketCount = configured ? await TicketCountAsync(userId, ct) : 0;
        if (ticketCount is null)
        {
            return View(new MemberTicketStatusViewModel(userId, configured, 0, null, null, HoldingsUnavailable: true)
            {
                SellsInHumans = sellsInHumans,
                HumansTicketCount = humansTickets,
            });
        }
        var eventYear = activeEvent is not null && activeEvent.Year > 0 ? activeEvent.Year : (int?)null;

        return View(new MemberTicketStatusViewModel(
            userId,
            configured,
            ticketCount.Value,
            eventYear,
            eventYear is null ? null : await ParticipationStatusAsync(userId, eventYear.Value, ct))
        {
            SellsInHumans = sellsInHumans,
            HumansTicketCount = humansTickets,
        });
    }

    /// <summary>
    /// Tickets the person holds (Valid, or Offered for resale until it sells) from Humans' own checkout: for the active
    /// event, and for any sale still open, matching the events the buy page offers.
    /// </summary>
    private static async Task<int> HumansTicketCountAsync(
        Services.Checkout.ITicketCheckoutService checkout, Guid userId, EventSettingsInfo? activeEvent,
        IReadOnlyList<Domain.Checkout.TicketSalesEvent> salesEvents, CancellationToken ct)
    {
        var forEvent = salesEvents
            .Where(e => activeEvent is null || e.EventSettingsId == activeEvent.Id || e.Mode != Domain.Checkout.TicketSalesMode.Closed)
            .Select(e => e.Id).ToHashSet();
        return (await checkout.GetTicketsForHolderAsync(userId, ct))
            .Count(t => t.State is Domain.Checkout.TicketCheckoutTicketState.Valid or Domain.Checkout.TicketCheckoutTicketState.Offered
                && forEvent.Contains(t.SalesEventId));
    }

    /// <summary>Null when the holdings read failed.</summary>
    private async Task<int?> TicketCountAsync(Guid userId, CancellationToken ct)
    {
        try
        {
            return (await ticketService.GetUserTicketHoldingsAsync(userId, ct)).TicketCount;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load ticket status for user {UserId}", userId);
            return null;
        }
    }

    private async Task<ParticipationStatus?> ParticipationStatusAsync(Guid userId, int year, CancellationToken ct)
    {
        try
        {
            return (await ticketService.GetParticipationsAsync(userId, ct))
                .FirstOrDefault(p => p.Year == year)?.Status;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load participation status for user {UserId}", userId);
            return null;
        }
    }
}

internal sealed record MemberTicketStatusViewModel(
    Guid UserId,
    bool TicketsConfigured,
    int UserTicketCount,
    int? EventYear,
    ParticipationStatus? ParticipationStatus,
    bool HoldingsUnavailable = false,
    bool SellsInHumans = false)
{
    public const string TicketPurchaseUrl = "https://tickets.nobodies.team";

    /// <summary>Held tickets (Valid or Offered) from Humans' own checkout, for the active event and any open sale; <see cref="UserTicketCount"/> counts the vendor sync's.</summary>
    public int HumansTicketCount { get; init; }

    public bool HasTicket => UserTicketCount + HumansTicketCount > 0;
}
