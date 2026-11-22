using Humans.Base.Authorization;
using Humans.Permissions.Contracts;
using Humans.Settings.Contracts;
using Humans.Teams.Contracts;
using Humans.Shifts.Contracts;
using Humans.Shifts.Services;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Shifts.ViewComponents;

/// <summary>
/// The team page's shift-staffing box, over the team and its sub-teams in the active event. Parameters bind from
/// <see cref="Humans.Teams.Contracts.TeamShiftsBoxArgs"/>.
/// </summary>
internal sealed class TeamShiftsBoxViewComponent(
    IShiftManagement shifts, ISettingsService settings, IPermissionService permissions) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(
        Guid teamId, string slug, IReadOnlyList<Guid> childTeamIds, Guid viewerUserId)
    {
        var ct = HttpContext.RequestAborted;
        var team = new Owner(TeamOwner.Kind, teamId);
        var canManage = await permissions.HasAsync(viewerUserId, ShiftsPermissions.Manage, team, ct)
            || await permissions.HasAsync(viewerUserId, ShiftsPermissions.Approve, team, ct);

        var activeEvent = settings.GetActiveEventSettings();
        var summary = activeEvent is null
            ? null
            : await shifts.GetShiftsSummaryAsync(activeEvent.Id, [teamId, .. childTeamIds], ct);

        return View(new TeamShiftsCardViewModel
        {
            TotalSlots = summary?.TotalSlots ?? 0,
            ConfirmedCount = summary?.ConfirmedCount ?? 0,
            PendingCount = summary?.PendingCount ?? 0,
            UniqueVolunteerCount = summary?.UniqueVolunteerCount ?? 0,
            ShiftsUrl = Url.Action("Index", "ShiftAdmin", new { slug })!,
            CanManageShifts = canManage,
            IncludesSubTeamCount = summary is null ? 0 : childTeamIds.Count(summary.TeamIdsWithShifts.Contains),
        });
    }
}

internal sealed record TeamShiftsCardViewModel
{
    public int TotalSlots { get; init; }
    public int ConfirmedCount { get; init; }
    public int PendingCount { get; init; }
    public int UniqueVolunteerCount { get; init; }
    public string ShiftsUrl { get; init; } = "";
    public bool CanManageShifts { get; init; }

    /// <summary>Sub-teams that have shifts.</summary>
    public int IncludesSubTeamCount { get; init; }
}
