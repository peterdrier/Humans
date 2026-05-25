using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Teams;
using Humans.Web.Models;
using Humans.Web.Models.Shifts;
using Microsoft.AspNetCore.Mvc;
using NodaTime;

namespace Humans.Web.ViewComponents;

public class ShiftSignupsViewComponent(
    IShiftView shiftView,
    IShiftManagementService shiftMgmt,
    ITeamServiceRead teamService,
    IClock clock,
    ILogger<ShiftSignupsViewComponent> logger) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(Guid userId, ShiftSignupsViewMode viewMode, string? displayName = null)
    {
        var model = new ShiftSignupsViewModel
        {
            ViewMode = viewMode,
            UserId = userId,
            DisplayName = displayName
        };

        try
        {
            var es = await shiftMgmt.GetActiveAsync();

            // T-10: signups come from the cached ShiftUserView (issue #720).
            // ShiftUserView.Signups is pre-filtered to the active event by the
            // inner ShiftViewService — no active event yields an empty list.
            var userView = await shiftView.GetUserAsync(userId);
            var signups = userView.Signups;

            var now = clock.GetCurrentInstant();
            model.EventSettings = es;

            var componentTeamIds = ShiftSignupBucketer.GetTeamIds(signups);
            IReadOnlyDictionary<Guid, string> componentTeamNames;
            if (componentTeamIds.Count == 0)
            {
                componentTeamNames = new Dictionary<Guid, string>();
            }
            else
            {
                var teamsById = await teamService.GetTeamsAsync();
                componentTeamNames = componentTeamIds
                    .Where(teamsById.ContainsKey)
                    .ToDictionary(id => id, id => teamsById[id].Name);
            }

            var buckets = ShiftSignupBucketer.Build(
                signups,
                es,
                componentTeamNames,
                now,
                includeOtherStatusesInPast: false);

            model.Upcoming = buckets.Upcoming;
            model.Pending = buckets.Pending;
            model.Past = buckets.Past;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading shift signups for user {UserId}", userId);
        }

        return View(model);
    }
}
