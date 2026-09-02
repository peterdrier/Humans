using Humans.Base.Authorization;
using Humans.Base.Controllers;
using Humans.Rideshare.Models;
using Humans.Rideshare.Services;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NodaTime;

namespace Humans.Rideshare.Controllers;

/// <summary>Admin-only: the active year's destination + travel windows, season stats, and the day roster.</summary>
[Authorize(Policy = PolicyNames.AdminOnly)]
[Route("Rideshare/Admin")]
internal sealed class RideshareAdminController(
    IRideshareService rideshare,
    IUserServiceRead users,
    IClock clock) : HumansControllerBase(users)
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var snapshot = await rideshare.GetSnapshotAsync(await rideshare.GetActiveYearAsync(ct), ct);
        return View(RideshareSettingsViewModel.From(snapshot));
    }

    [HttpPost("")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Index(RideshareSettingsViewModel model, CancellationToken ct)
    {
        var (error, user) = await ResolveCurrentUserOrChallengeAsync(ct);
        if (error is not null) return error;

        var year = await rideshare.GetActiveYearAsync(ct);
        var save = model.ToSave();
        if (save is null) ModelState.AddModelError(string.Empty, "Every window date must be a valid date (yyyy-mm-dd).");
        if (!ModelState.IsValid)
        {
            model.Stats = (await rideshare.GetSnapshotAsync(year, ct)).Stats();
            return View(model);
        }

        await rideshare.SaveSettingsAsync(year, save!, user.Id, ct);
        SetSuccess($"Rideshare settings for {year} saved.");
        return RedirectToAction(nameof(Index));
    }

    [HttpGet("Day")]
    public async Task<IActionResult> Day(string? date, CancellationToken ct)
    {
        var snapshot = await rideshare.GetSnapshotAsync(await rideshare.GetActiveYearAsync(ct), ct);
        var day = RideshareDates.Parse(date) ?? RideshareDates.Today(clock);
        return View(DayViewModel.Build(snapshot, day));
    }
}
