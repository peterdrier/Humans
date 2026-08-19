using System.Security.Claims;
using Humans.Shifts.Contracts;
using Humans.Tickets.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Humans.Tickets.ViewComponents;

/// <summary>
/// The member dashboard's ticket card: confirmed, not attending, or missing — plus the
/// buy/declare actions that go with each. Shows a "not configured" note when no ticket
/// vendor is set up, rather than hiding.
/// </summary>
public sealed class MemberTicketStatusViewComponent(
    ITicketServiceRead ticketService,
    IUserServiceRead userService,
    IBurnSettingsService burnSettings,
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
        var configured = ticketSettings.Value.IsConfigured;
        var ticketCount = configured ? await TicketCountAsync(userId, ct) : 0;
        var eventYear = await ActiveEventYearAsync(ct);

        return View(new MemberTicketStatusViewModel(
            userId,
            configured,
            ticketCount,
            eventYear,
            eventYear is null ? null : await ParticipationStatusAsync(userId, eventYear.Value, ct)));
    }

    private async Task<int> TicketCountAsync(Guid userId, CancellationToken ct)
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
            return 0;
        }
    }

    private async Task<int?> ActiveEventYearAsync(CancellationToken ct)
    {
        try
        {
            var activeEvent = await burnSettings.GetActiveAsync(ct);
            return activeEvent is not null && activeEvent.Year > 0 ? activeEvent.Year : null;
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
            var info = await userService.GetUserInfoAsync(userId, ct);
            return info?.EventParticipations.FirstOrDefault(p => p.Year == year)?.Status;
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
    ParticipationStatus? ParticipationStatus)
{
    public const string TicketPurchaseUrl = "https://tickets.nobodies.team";

    public bool HasTicket => UserTicketCount > 0;
}
