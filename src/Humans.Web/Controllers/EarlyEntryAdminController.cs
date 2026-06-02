using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Web.Authorization;
using Humans.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NodaTime.Text;

namespace Humans.Web.Controllers;

/// <summary>
/// Global, team-independent Art Early Entry admin console. Mirrors the Cantina
/// surface: a role-gated page reached from the admin nav, spanning every
/// early-entry-enabled team rather than a single team. Mutations reuse the
/// Teams-section grant methods on <see cref="ITeamService"/> (team-scoped); the
/// per-team page at <c>TeamAdminController</c> coexists with this console.
///
/// Authorization gate: the <see cref="PolicyNames.EarlyEntryArtAdminOrAdmin"/>
/// policy (Admin or the grantable EarlyEntryArtAdmin role).
/// </summary>
[Authorize(Policy = PolicyNames.EarlyEntryArtAdminOrAdmin)]
[Route("EarlyEntry")]
public sealed class EarlyEntryAdminController : HumansControllerBase
{
    private readonly ITeamService _teamService;

    public EarlyEntryAdminController(IUserServiceRead userService, ITeamService teamService)
        : base(userService)
    {
        _teamService = teamService;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var vm = await BuildIndexAsync(ct);
        return View(vm);
    }

    [HttpPost("Add")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Add(AddEarlyEntryConsoleInput input, CancellationToken ct)
    {
        var parsed = LocalDatePattern.Iso.Parse(input.EntryDate);
        if (!ModelState.IsValid || !parsed.Success)
        {
            if (!parsed.Success)
                ModelState.AddModelError(nameof(input.EntryDate), "Enter a valid date (yyyy-MM-dd).");
            return View(nameof(Index), await BuildIndexAsync(ct));
        }

        await _teamService.AddEarlyEntryGrantAsync(
            input.TeamId, input.UserId, parsed.Value, input.ProjectName, GetCurrentUserId()!.Value, ct);
        SetSuccess("Early entry granted.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("Edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(EditEarlyEntryConsoleInput input, CancellationToken ct)
    {
        var parsed = LocalDatePattern.Iso.Parse(input.EntryDate);
        if (!ModelState.IsValid || !parsed.Success)
        {
            if (!parsed.Success)
                ModelState.AddModelError(nameof(input.EntryDate), "Enter a valid date (yyyy-MM-dd).");
            return View(nameof(Index), await BuildIndexAsync(ct));
        }

        await _teamService.EditEarlyEntryGrantAsync(
            input.TeamId, input.GrantId, parsed.Value, input.ProjectName, GetCurrentUserId()!.Value, ct);
        SetSuccess("Early entry updated.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("Remove")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Remove(Guid teamId, Guid grantId, CancellationToken ct)
    {
        await _teamService.RemoveEarlyEntryGrantAsync(teamId, grantId, GetCurrentUserId()!.Value, ct);
        SetSuccess("Early entry revoked.");
        return RedirectToAction(nameof(Index));
    }

    private async Task<EarlyEntryConsoleViewModel> BuildIndexAsync(CancellationToken ct)
    {
        var grants = await _teamService.GetAllEarlyEntryGrantsAsync(ct);
        var teams = await _teamService.GetTeamsAsync(ct);
        var humans = await UserService.GetUserInfosAsync(
            grants.Select(g => g.UserId).Distinct().ToList(), ct);

        // Display sort is a presentation concern (peters-hard-rules: controllers sort).
        var rows = grants
            .Select(g => new EarlyEntryConsoleRowViewModel
            {
                GrantId = g.Id,
                TeamId = g.TeamId,
                TeamName = teams.GetValueOrDefault(g.TeamId)?.Name ?? "",
                HumanName = humans.GetValueOrDefault(g.UserId)?.BurnerName ?? "",
                EntryDate = g.EntryDate,
                ProjectName = g.ProjectName,
            })
            .OrderBy(r => r.TeamName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.ProjectName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.EntryDate)
            .ToList();

        var teamOptions = teams.Values
            .Where(t => t.EarlyEntryEnabled)
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Select(t => new EarlyEntryTeamOption { Id = t.Id, Name = t.Name })
            .ToList();

        return new EarlyEntryConsoleViewModel { Grants = rows, Teams = teamOptions };
    }
}
