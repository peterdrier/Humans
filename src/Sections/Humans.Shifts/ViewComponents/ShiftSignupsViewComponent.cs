using Humans.Shifts.Contracts;
using Humans.Base.Enums;
using Humans.Shifts.Models;
using Humans.Teams.Contracts;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using Humans.Shifts.Services;

namespace Humans.Shifts.ViewComponents;

/// <summary>Rendered by type (<c>Component.InvokeAsync</c>), so it can stay internal.</summary>
internal sealed class ShiftSignupsViewComponent(
    IShiftView shiftView,
    IBurnSettingsService burnSettings,
    ITeamService teamService,
    IClock clock,
    ILogger<ShiftSignupsViewComponent> logger) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(Guid userId, ProfileCardViewMode viewMode)
    {
        var model = new ShiftSignupsViewModel
        {
            ViewMode = viewMode,
            UserId = userId
        };

        try
        {
            var ct = HttpContext.RequestAborted;
            var es = await burnSettings.GetActiveAsync(ct);

            // Already scoped to the active event; none yields an empty list.
            var userView = await shiftView.GetUserAsync(userId, ct);
            var signups = userView.Signups;

            var now = clock.GetCurrentInstant();
            model.EventSettings = es;

            var teamIds = signups.Select(s => s.TeamId).Distinct().ToList();
            IReadOnlyDictionary<Guid, string> teamNames;
            if (teamIds.Count == 0)
            {
                teamNames = new Dictionary<Guid, string>();
            }
            else
            {
                var teamsById = await teamService.GetTeamsAsync(ct);
                teamNames = teamIds
                    .Where(teamsById.ContainsKey)
                    .ToDictionary(id => id, id => teamsById[id].Name);
            }

            if (es is not null)
            {
                var buckets = ShiftSignupSummaryBucketer.Build(signups, teamNames, now);
                model.Upcoming = buckets.Upcoming;
                model.Pending = buckets.Pending;
                model.Past = buckets.Past;
            }
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading shift signups for user {UserId}", userId);
        }

        return View(model);
    }
}
