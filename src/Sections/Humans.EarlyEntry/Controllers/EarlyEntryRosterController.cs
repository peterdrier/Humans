using Humans.EarlyEntry.Contracts;
using Humans.EarlyEntry.Models;
using Humans.Base.Authorization;
using Humans.Base.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Humans.Users.Contracts;

namespace Humans.EarlyEntry.Controllers;

[Route("Shifts/Admin/EarlyEntry")]
[Authorize(Policy = PolicyNames.ShiftDashboardAccess)]
internal sealed class EarlyEntryRosterController(
    IEarlyEntryService earlyEntryService,
    IUserServiceRead userService) : HumansControllerBase(userService)
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct = default)
    {
        var rows = await earlyEntryService.GetRosterAsync(ct);

        var ordered = rows
            .OrderBy(r => r.EarliestEntryDate)
            .ThenBy(r => r.UserId)
            .ToList();

        // Burner name renders from UserId via <vc:human>; no DisplayName field
        // (memory/code/no-new-displayname-fields.md). Legal name is the separate read below.
        var vms = new List<EarlyEntryRosterRowVm>(ordered.Count);
        foreach (var r in ordered)
        {
            var info = await FindUserInfoByIdAsync(r.UserId, ct);
            var legalName = info?.Profile?.FullName ?? string.Empty;
            vms.Add(new EarlyEntryRosterRowVm(r.UserId, legalName, r.EarliestEntryDate, r.Sources, r.HasMultiple));
        }

        return View(new EarlyEntryRosterViewModel(vms));
    }
}
