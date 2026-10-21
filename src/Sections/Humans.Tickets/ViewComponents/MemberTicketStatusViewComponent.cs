using System.Security.Claims;
using Humans.Settings.Contracts;
using Humans.Tickets.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Humans.Tickets.ViewComponents;

/// <summary>
/// The member dashboard's ticket card: confirmed, not attending, or missing — plus the
/// buy/declare actions that go with each. When the holdings read fails the card says the
/// status is unavailable instead of claiming "no ticket" and offering buy/declare. Shows a "not configured" note when no ticket
/// vendor is set up, rather than hiding. Renders nothing off season (the admin switch on
/// the active event settings): there is no ticket to buy or declare against.
/// </summary>
public sealed class MemberTicketStatusViewComponent(
    ITicketService ticketService,
    ISettingsService settingsService,
    IOptions<TicketVendorSettings> ticketSettings,
    ILogger<MemberTicketStatusViewComponent> logger) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        if (!Guid.TryParse(UserClaimsPrincipal.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
        {
            return Content(string.Empty);
        }

        var ct = HttpContext.RequestAborted;
        var activeEvent = await ActiveEventAsync(ct);
        if (activeEvent?.IsOffSeason == true)
        {
            return Content(string.Empty);
        }

        var configured = ticketSettings.Value.IsConfigured;
        var ticketCount = configured ? await TicketCountAsync(userId, ct) : 0;
        if (ticketCount is null)
        {
            return View(new MemberTicketStatusViewModel(userId, configured, 0, null, null, HoldingsUnavailable: true));
        }
        var eventYear = activeEvent is not null && activeEvent.Year > 0 ? activeEvent.Year : (int?)null;

        return View(new MemberTicketStatusViewModel(
            userId,
            configured,
            ticketCount.Value,
            eventYear,
            eventYear is null ? null : await ParticipationStatusAsync(userId, eventYear.Value, ct)));
    }

    /// <summary>The member's ticket count, or <c>null</c> when the holdings read failed.</summary>
    private async Task<int?> TicketCountAsync(Guid userId, CancellationToken ct)
    {
        try
        {
            return (await ticketService.GetUserTicketHoldingsAsync(userId, ct)).TicketCount;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // request aborted — let it abort, don't log as an error
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load ticket status for user {UserId}", userId);
            return null;
        }
    }

    private async Task<EventSettingsInfo?> ActiveEventAsync(CancellationToken ct)
    {
        try
        {
            return await settingsService.GetActiveEventSettingsAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // request aborted — let it abort, don't log as an error
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load active event for the dashboard ticket card");
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
            throw; // request aborted — let it abort, don't log as an error
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
    bool HoldingsUnavailable = false)
{
    public const string TicketPurchaseUrl = "https://tickets.nobodies.team";

    public bool HasTicket => UserTicketCount > 0;
}
