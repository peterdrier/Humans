using Humans.Base.Controllers;
using Humans.Settings.Contracts;
using Humans.Tickets.Services;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

namespace Humans.Tickets.Controllers;

/// <summary>
/// The member's own "not attending this year" declaration, posted from the dashboard ticket card.
/// </summary>
/// <remarks>
/// Writes <c>tickets_event_participations</c>, a Tickets table. Owner-only: every action acts on the signed-in
/// member.
/// </remarks>
[Authorize]
[Route("Tickets")]
internal sealed class TicketParticipationController(
    IEventParticipationManagement participations,
    ISettingsService settingsService,
    IUserService userService,
    IStringLocalizer<TicketsResource> localizer,
    ILogger<TicketParticipationController> logger) : HumansControllerBase(userService)
{
    [HttpPost("DeclareNotAttending")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeclareNotAttending()
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        try
        {
            var eventYear = await GetActiveEventYearOrSetErrorAsync();
            if (eventYear is null)
            {
                return Redirect("/");
            }

            await participations.DeclareNotAttendingAsync(user.Id, eventYear.Value);
            SetSuccess(localizer["Tickets_NotAttending_Declared"].Value);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to declare not attending for user {UserId}", user.Id);
            SetError(localizer["Tickets_NotAttending_Failed"].Value);
        }

        return Redirect("/");
    }

    [HttpPost("UndoNotAttending")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UndoNotAttending()
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        try
        {
            var eventYear = await GetActiveEventYearOrSetErrorAsync();
            if (eventYear is null)
            {
                return Redirect("/");
            }

            if (await participations.UndoNotAttendingAsync(user.Id, eventYear.Value))
                SetSuccess(localizer["Tickets_NotAttending_Undone"].Value);
            else
                SetError(localizer["Tickets_NotAttending_UndoFailed"].Value);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to undo not attending for user {UserId}", user.Id);
            SetError(localizer["Tickets_NotAttending_Failed"].Value);
        }

        return Redirect("/");
    }

    private async Task<int?> GetActiveEventYearOrSetErrorAsync()
    {
        var activeEvent = await settingsService.GetActiveEventSettingsAsync();
        if (activeEvent is not null && activeEvent.Year > 0)
        {
            return activeEvent.Year;
        }

        SetError(localizer["Tickets_NotAttending_NoActiveEvent"].Value);
        return null;
    }
}
