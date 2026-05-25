using Humans.Application.Interfaces.EarlyEntry;
using Humans.Application.Interfaces.Users;
using Humans.Web.Authorization;
using Humans.Web.Models.EarlyEntry;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.Controllers;

[Route("Shifts/Admin/EarlyEntry")]
[Authorize(Policy = PolicyNames.ShiftDashboardAccess)]
public sealed class EarlyEntryRosterController(
    IEarlyEntryService earlyEntryService,
    IUserServiceRead userService) : HumansControllerBase(userService)
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct = default)
    {
        var rows = await earlyEntryService.GetRosterAsync(ct);

        // Names are resolved at render via <vc:human>; the VM carries UserId only
        // (no DisplayName field — see memory/code/no-new-displayname-fields.md).
        var ordered = rows
            .OrderBy(r => r.EarliestEntryDate)
            .ThenBy(r => r.UserId)
            .Select(r => new EarlyEntryRosterRowVm(r.UserId, r.EarliestEntryDate, r.Sources, r.HasMultiple))
            .ToList();

        return View(new EarlyEntryRosterViewModel(ordered));
    }
}
