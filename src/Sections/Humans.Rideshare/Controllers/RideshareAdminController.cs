using Humans.Rideshare.Contracts;
using Humans.Base.Authorization;
using Humans.Base.Controllers;
using Humans.Rideshare.Models;
using Humans.Rideshare.Services;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using NodaTime;

namespace Humans.Rideshare.Controllers;

[Authorize(Policy = RidesharePolicies.Read)]
[Route("Rideshare/Admin")]
internal sealed class RideshareAdminController(
    IRideshareService rideshare,
    IUserService users,
    IStringLocalizer<RideshareResource> localizer,
    IClock clock,
    ILogger<RideshareAdminController> logger) : HumansControllerBase(users)
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var snapshot = await rideshare.GetSnapshotAsync(await rideshare.GetActiveEventIdAsync(ct), ct);
        return View(RideshareSettingsViewModel.From(snapshot));
    }

    [HttpPost("")]
    [Authorize(Policy = RidesharePolicies.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Index(RideshareSettingsViewModel model, CancellationToken ct)
    {
        var (error, user) = await ResolveCurrentUserOrChallengeAsync(ct);
        if (error is not null) return error;

        var eventSettingsId = model.EventSettingsId;
        var save = ModelState.IsValid ? model.ToSave() : null;
        if (save is null)
        {
            if (ModelState.IsValid) ModelState.AddModelError(string.Empty, "Every window date must be a valid date (yyyy-mm-dd).");
            model.Stats = (await rideshare.GetSnapshotAsync(eventSettingsId, ct)).Stats();
            return View(model);
        }

        var result = await rideshare.SaveSettingsAsync(eventSettingsId, save, user.Id, ct);
        if (result.Refusal is { } refusal)
        {
            logger.LogWarning("Rideshare settings save for event {EventSettingsId} rejected: rule {Rule}", eventSettingsId, refusal.Key);
            ModelState.AddModelError(string.Empty, localizer[refusal.Key, refusal.Args]);
            model.Stats = (await rideshare.GetSnapshotAsync(eventSettingsId, ct)).Stats();
            return View(model);
        }

        SetSuccess($"Rideshare settings for {model.EventName} saved.");
        return RedirectToAction(nameof(Index));
    }

    [HttpGet("Day")]
    public async Task<IActionResult> Day(string? date, CancellationToken ct)
    {
        var snapshot = await rideshare.GetSnapshotAsync(await rideshare.GetActiveEventIdAsync(ct), ct);
        var day = RideshareDates.Parse(date) ?? RideshareDates.Today(clock);
        return View(DayViewModel.Build(snapshot, day));
    }
}
