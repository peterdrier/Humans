using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Tickets;
using Humans.Application.Interfaces.Users;
using Humans.Web.Authorization;
using Humans.Web.Models.Tickets;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.Controllers;

/// <summary>
/// "Who's onsite" view (#736). Read-only flat list of every human with an
/// Attended + non-null CheckedInAt EventParticipation for the active event
/// year. Camp / team / governance-role filtering + name stitching are
/// delegated to <see cref="IOnsiteRosterService"/>; this controller stays a
/// thin HTTP adapter per
/// <c>memory/architecture/no-business-logic-in-controllers.md</c>.
/// </summary>
[Authorize(Policy = PolicyNames.TicketAdminBoardOrAdmin)]
[Route("Tickets/Admin/Onsite")]
public sealed class TicketsOnsiteAdminController(
    IUserServiceRead userService,
    IShiftManagementService shifts,
    IOnsiteRosterService roster) : HumansControllerBase(userService)
{
    [HttpGet("")]
    public async Task<IActionResult> Index(
        [FromQuery] string? camp,
        [FromQuery] string? team,
        [FromQuery] string? role,
        CancellationToken ct)
    {
        var active = await shifts.GetActiveAsync();
        var year = active?.Year ?? 0;

        var result = await roster.GetRosterAsync(year, camp, team, role, ct);

        // Display sort lives at the presentation layer per
        // memory/architecture/display-sort-in-controllers.md — most-recent
        // check-in first.
        var rows = result.Rows
            .OrderByDescending(r => r.CheckedInAt)
            .ToList();

        var vm = new OnsiteRosterViewModel(
            Year: year,
            CampFilter: camp,
            TeamFilter: team,
            RoleFilter: role,
            AvailableCamps: result.AvailableCamps,
            AvailableTeams: result.AvailableTeams,
            AvailableRoles: result.AvailableRoles,
            Rows: rows);

        return View("~/Views/Tickets/Admin/Onsite.cshtml", vm);
    }
}
