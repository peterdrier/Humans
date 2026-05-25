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
    private readonly IUserServiceRead _userService = userService;

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct = default)
    {
        var rows = await earlyEntryService.GetRosterAsync(ct);

        var vmRows = new List<EarlyEntryRosterRowVm>(rows.Count);
        foreach (var r in rows)
        {
            var info = await _userService.GetUserInfoAsync(r.UserId, ct);
            vmRows.Add(new EarlyEntryRosterRowVm(
                info?.BurnerName ?? "(unknown)",
                r.EarliestEntryDate,
                r.Sources,
                r.HasMultiple));
        }

        var ordered = vmRows
            .OrderBy(r => r.EarliestEntryDate)
            .ThenBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return View(new EarlyEntryRosterViewModel(ordered));
    }
}
