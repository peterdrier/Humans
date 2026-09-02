using Humans.Base.Authorization;
using Humans.Base.Controllers;
using Humans.Rideshare.Models;
using Humans.Rideshare.Services;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Rideshare.Controllers;

/// <summary>Admin-only: the active year's destination + travel windows, season stats, and the day roster.</summary>
[Authorize(Policy = PolicyNames.AdminOnly)]
[Route("Rideshare/Admin")]
internal sealed class RideshareAdminController(
    IRideshareService rideshare,
    IUserServiceRead users,
    IStringLocalizer<RideshareResource> localizer,
    IClock clock,
    ILogger<RideshareAdminController> logger) : HumansControllerBase(users)
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
        var save = ModelState.IsValid ? model.ToSave() : null;
        if (save is null)
        {
            if (ModelState.IsValid) ModelState.AddModelError(string.Empty, "Every window date must be a valid date (yyyy-mm-dd).");
            model.Stats = (await rideshare.GetSnapshotAsync(year, ct)).Stats();
            return View(model);
        }

        try
        {
            await rideshare.SaveSettingsAsync(year, save, user.Id, ct);
        }
        catch (RideshareRuleException ex)
        {
            logger.LogInformation(ex, "Rideshare settings save for {Year} rejected: rule {Rule}", year, ex.Key);
            ModelState.AddModelError(string.Empty, localizer[ex.Key, ex.Args]);
            model.Stats = (await rideshare.GetSnapshotAsync(year, ct)).Stats();
            return View(model);
        }

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
