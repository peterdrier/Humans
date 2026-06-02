using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Web.Authorization;
using Humans.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NodaTime.Text;

namespace Humans.Web.Controllers;

/// <summary>
/// Art Early Entry admin console. Targets a SINGLE team automatically — the one
/// the admin has marked with <see cref="TeamInfo.EarlyEntryEnabled"/> (via the
/// Edit-Team checkbox). There is no team picker: the console resolves its target
/// as the unique early-entry-enabled team server-side. Mutations reuse the
/// Teams-section grant methods on <see cref="ITeamService"/> (team-scoped) with
/// the resolved team's id; the per-team page at <c>TeamAdminController</c>
/// coexists with this console.
///
/// Authorization gate: the <see cref="PolicyNames.EarlyEntryArtAdminOrAdmin"/>
/// policy (Admin or the grantable EarlyEntryArtAdmin role).
/// </summary>
[Authorize(Policy = PolicyNames.EarlyEntryArtAdminOrAdmin)]
[Route("EarlyEntry")]
public sealed class EarlyEntryAdminController : HumansControllerBase
{
    private const string NotConfiguredError =
        "Early entry isn't set up on exactly one team — fix that first.";

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
        var (team, _) = await ResolveTargetTeamAsync(ct);
        if (team is null)
        {
            SetError(NotConfiguredError);
            return RedirectToAction(nameof(Index));
        }

        var parsed = LocalDatePattern.Iso.Parse(input.EntryDate);
        if (!ModelState.IsValid || !parsed.Success)
        {
            if (!parsed.Success)
                ModelState.AddModelError(nameof(input.EntryDate), "Enter a valid date (yyyy-MM-dd).");
            return View(nameof(Index), await BuildIndexAsync(ct));
        }

        await _teamService.AddEarlyEntryGrantAsync(
            team.Id, input.UserId, parsed.Value, input.ProjectName, GetCurrentUserId()!.Value, ct);
        SetSuccess("Early entry granted.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("Edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(EditEarlyEntryConsoleInput input, CancellationToken ct)
    {
        var (team, _) = await ResolveTargetTeamAsync(ct);
        if (team is null)
        {
            SetError(NotConfiguredError);
            return RedirectToAction(nameof(Index));
        }

        var parsed = LocalDatePattern.Iso.Parse(input.EntryDate);
        if (!ModelState.IsValid || !parsed.Success)
        {
            if (!parsed.Success)
                ModelState.AddModelError(nameof(input.EntryDate), "Enter a valid date (yyyy-MM-dd).");
            return View(nameof(Index), await BuildIndexAsync(ct));
        }

        await _teamService.EditEarlyEntryGrantAsync(
            team.Id, input.GrantId, parsed.Value, input.ProjectName, GetCurrentUserId()!.Value, ct);
        SetSuccess("Early entry updated.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("Remove")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Remove(Guid grantId, CancellationToken ct)
    {
        var (team, _) = await ResolveTargetTeamAsync(ct);
        if (team is null)
        {
            SetError(NotConfiguredError);
            return RedirectToAction(nameof(Index));
        }

        await _teamService.RemoveEarlyEntryGrantAsync(team.Id, grantId, GetCurrentUserId()!.Value, ct);
        SetSuccess("Early entry revoked.");
        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Resolves the console's target team server-side: the unique team with
    /// early entry enabled. Returns (the team, count of enabled teams) — the
    /// team is null unless exactly one is enabled, and the count drives the
    /// not-configured messaging.
    /// </summary>
    private async Task<(TeamInfo? Team, int EnabledCount)> ResolveTargetTeamAsync(CancellationToken ct)
    {
        var enabled = (await _teamService.GetTeamsAsync(ct)).Values
            .Where(t => t.EarlyEntryEnabled).ToList();
        return (enabled.Count == 1 ? enabled[0] : null, enabled.Count);
    }

    private async Task<EarlyEntryConsoleViewModel> BuildIndexAsync(CancellationToken ct)
    {
        var (team, enabledCount) = await ResolveTargetTeamAsync(ct);
        if (team is null)
        {
            return new EarlyEntryConsoleViewModel
            {
                IsConfigured = false,
                ConfigMessage = enabledCount == 0
                    ? "Early entry isn't set up yet — an admin must enable it on one team (Edit Team → Enable Early Entry)."
                    : $"Early entry is enabled on {enabledCount} teams; it's meant for exactly one. Disable it on the extras.",
            };
        }

        var grants = await _teamService.GetEarlyEntryGrantsForTeamAsync(team.Id, ct);
        var humans = await UserService.GetUserInfosAsync(
            grants.Select(g => g.UserId).Distinct().ToList(), ct);

        // Display sort is a presentation concern (peters-hard-rules: controllers sort).
        var rows = grants
            .Select(g => new EarlyEntryConsoleRowViewModel
            {
                GrantId = g.Id,
                HumanName = humans.GetValueOrDefault(g.UserId)?.BurnerName ?? "",
                EntryDate = g.EntryDate,
                ProjectName = g.ProjectName,
            })
            .OrderBy(r => r.ProjectName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.EntryDate)
            .ToList();

        return new EarlyEntryConsoleViewModel
        {
            Grants = rows,
            TeamName = team.Name,
            IsConfigured = true,
        };
    }
}
