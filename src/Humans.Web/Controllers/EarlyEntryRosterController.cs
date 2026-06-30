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

        // Burner name is resolved at render via <vc:human>; the VM carries UserId for that
        // (no DisplayName field — see memory/code/no-new-displayname-fields.md). Legal name is
        // a separate concept resolved here from the canonical read-model.
        var ordered = rows
            .OrderBy(r => r.EarliestEntryDate)
            .ThenBy(r => r.UserId)
            .ToList();

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
