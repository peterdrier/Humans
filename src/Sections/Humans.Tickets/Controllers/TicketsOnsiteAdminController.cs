using Humans.Settings.Contracts;
using Humans.Tickets.Services;
using Humans.Base.Authorization;
using Humans.Base.Controllers;
using Humans.Tickets.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Humans.Users.Contracts;

namespace Humans.Tickets.Controllers;

/// <summary>ScannerAccess, not TicketsPolicies.Read, so the gate-terminal account can check the roster from the door.</summary>
[Authorize(Policy = PolicyNames.ScannerAccess)]
[Route("Tickets/Admin/Onsite")]
internal sealed class TicketsOnsiteAdminController(
    IUserService userService,
    ISettingsService settingsService,
    IOnsiteRosterService roster) : HumansControllerBase(userService)
{
    [HttpGet("")]
    public async Task<IActionResult> Index(
        [FromQuery] string? camp,
        [FromQuery] string? team,
        [FromQuery] string? role,
        CancellationToken ct)
    {
        var active = await settingsService.GetActiveEventSettingsAsync(ct);
        var year = active?.Year ?? 0;

        var result = await roster.GetRosterAsync(year, camp, team, role, ct);

        var rows = result.Rows
            .OrderByDescending(r => r.CheckedInAt)
            .Select(r => r with
            {
                CampNames = r.CampNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(),
                TeamNames = r.TeamNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(),
                RoleNames = r.RoleNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList()
            })
            .ToList();

        var vm = new OnsiteRosterViewModel(
            Year: year,
            CampFilter: camp,
            TeamFilter: team,
            RoleFilter: role,
            AvailableCamps: result.AvailableCamps.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(),
            AvailableTeams: result.AvailableTeams.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(),
            AvailableRoles: result.AvailableRoles.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(),
            Rows: rows);

        return View("~/Views/Tickets/Admin/Onsite.cshtml", vm);
    }
}
