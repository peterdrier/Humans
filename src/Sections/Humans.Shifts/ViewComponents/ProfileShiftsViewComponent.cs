using System.Security.Claims;
using Humans.Base.Enums;
using Humans.Base.Extensions;
using Humans.Shifts.Contracts;
using Humans.Shifts.Models;
using Humans.Shifts.Services;
using Humans.Teams.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Mvc;
using NodaTime;

namespace Humans.Shifts.ViewComponents;

/// <summary>
/// Another member's shifts on their profile: signups, build strip and no-show history. Only a coordinator or a
/// privileged signup approver sees it, never on one's own profile.
/// </summary>
internal sealed class ProfileShiftsViewComponent(
    IShiftSignupService signups,
    IShiftService shifts,
    ITeamService teams,
    IUserService users) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(Guid userId, ProfileCardViewMode viewMode)
    {
        if (viewMode == ProfileCardViewMode.Self) return Content(string.Empty);

        var ct = HttpContext.RequestAborted;
        if (!ShiftsRoles.IsPrivilegedSignupApprover(UserClaimsPrincipal) && !await IsCoordinatorAsync(ct))
            return Content(string.Empty);

        var noShows = await signups.GetNoShowHistoryAsync(userId, ct);
        var teamNames = noShows.Count == 0
            ? new Dictionary<Guid, string>()
            : (await teams.GetTeamsAsync(ct)).ToDictionary(t => t.Key, t => t.Value.Name);

        var history = noShows.OrderByDescending(s => s.ReviewedAt).Select(s =>
        {
            var zone = DateTimeZoneProviders.Tzdb[s.TimeZoneId];
            return new NoShowHistoryItem(
                s.ShiftLabel,
                teamNames.GetValueOrDefault(s.TeamId, ""),
                s.ShiftStart.InZone(zone).ToDateTimeUnspecified().ToMonthDayTime(),
                s.ReviewedByUserId,
                s.ReviewedAt?.InZone(zone).ToDateTimeUnspecified().ToMonthDayTime());
        }).ToList();

        return View(new ProfileShiftsViewModel(userId, history));
    }

    private async Task<bool> IsCoordinatorAsync(CancellationToken ct)
    {
        if (!Guid.TryParse(UserClaimsPrincipal.FindFirstValue(ClaimTypes.NameIdentifier), out var claimId))
            return false;
        // A session from a merged-away account asks as the survivor.
        var viewer = await users.GetUserInfoAsync(claimId, ct);
        return viewer is not null && (await shifts.GetCoordinatorTeamIdsAsync(viewer.Id, ct)).Count > 0;
    }
}
