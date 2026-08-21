using Humans.Shifts.Models;
using Humans.Teams.Contracts;
using Microsoft.AspNetCore.Mvc;
using NodaTime;

namespace Humans.Shifts.Contracts;

/// <summary>
/// The "Shift Signups" card — a user's upcoming, pending and past signups in the active
/// event. Rendered as <c>&lt;vc:shift-signups&gt;</c> from Shell's dashboard, profile,
/// user-admin detail and widget-gallery pages.
/// </summary>
/// <remarks>
/// Public, under <c>Contracts/</c>, so MVC's default provider discovers it and the tag
/// helper is generated at compile time — that is what keeps the four call sites unchanged
/// (design §15 step 6; HUM0034's carve-out is the folder). Its constructor is therefore
/// all-public, which is why it reads the leaf <see cref="IShiftView"/> rather than the
/// section-internal <c>IShiftRowView</c>: a public constructor cannot take an internal
/// parameter (CS0051). No fidelity is lost — every field this card renders is already
/// resolved on <see cref="ShiftSignupSummary"/>, so the entity walk the pre-G5 version did
/// (<c>item.Signup.Shift.Rota.Name</c>, <c>shift.DayOffset</c>) was re-deriving what the
/// projection had already worked out (nobodies-collective/Humans#866).
/// </remarks>
public sealed class ShiftSignupsViewComponent(
    IShiftView shiftView,
    IBurnSettingsService burnSettings,
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
            var es = await burnSettings.GetActiveAsync();

            // T-10: signups come from the cached shift view (issue #720). The summary's
            // Signups list is pre-filtered to the active event by the inner
            // ShiftViewService — no active event yields an empty list.
            var userView = await shiftView.GetUserAsync(userId);
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
                var teamsById = await teamService.GetTeamsAsync();
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
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading shift signups for user {UserId}", userId);
        }

        return View(model);
    }
}

/// <summary>
/// Which side of the card is being rendered: the volunteer's own view, or an
/// administrator looking at someone else's.
/// </summary>
/// <remarks>
/// Public because it is a parameter of <see cref="ShiftSignupsViewComponent.InvokeAsync"/>,
/// which the four Shell call sites bind through <c>view-mode="…"</c>.
/// </remarks>
public enum ShiftSignupsViewMode
{
    Self,
    Admin
}
