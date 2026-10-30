using Humans.Teams.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Teams.ViewComponents;

/// <summary>
/// The widget gallery's Teams samples: synthetic staffing numbers for the summary card, and the search-result row keyed
/// off the first real team (it renders nothing for an unresolved key); null when the environment holds none.
/// </summary>
internal sealed class TeamsGalleryViewComponent(ITeamService teamService) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        var teams = await teamService.GetTeamsAsync(HttpContext.RequestAborted);
        var summary = new ShiftsSummaryCardViewModel
        {
            TotalSlots = 24,
            ConfirmedCount = 17,
            PendingCount = 3,
            UniqueVolunteerCount = 12,
            ShiftsUrl = Url.Action("Index", "Shifts") ?? "#",
            CanManageShifts = true,
            IncludesSubTeamCount = 2,
        };
        return View(new TeamsGalleryViewModel(teams.Values.FirstOrDefault()?.Id, summary));
    }
}

internal sealed record TeamsGalleryViewModel(Guid? TeamId, ShiftsSummaryCardViewModel ShiftsSummary);
