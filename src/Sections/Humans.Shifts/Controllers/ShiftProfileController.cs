using Humans.Shifts.Contracts;
using Humans.Shifts.Models;
using Humans.Shifts.Services;
using Humans.Base;
using Humans.Base.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Humans.Users.Contracts;

namespace Humans.Shifts.Controllers;

[Authorize]
[Route("Shifts")]
internal sealed class ShiftProfileController(
    IShiftManagement shiftMgmt,
    IShiftVolunteerProfiles shiftProfiles,
    IShiftView shiftView,
    IUserService userService,
    // Its one string, Profile_Updated, is Shell's profile vocabulary.
    IStringLocalizer<SharedResource> localizer,
    IStringLocalizer<ShiftsResource> shiftsLocalizer,
    ILogger<ShiftProfileController> logger) : HumansControllerBase(userService)
{
    [HttpGet("ShiftInfo")]
    public async Task<IActionResult> ShiftInfo()
    {
        try
        {
            var user = await GetCurrentUserInfoAsync();
            if (user is null)
                return NotFound();
            var profile = await shiftMgmt.GetShiftProfileAsync(user.Id);
            var model = ShiftInfoViewModel.FromProfile(profile);
            await PopulateTagsAsync(model, user.Id);
            return View(model);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load shift info for user");
            SetError(shiftsLocalizer["Shifts_ShiftProfile_LoadFailed"].Value);
            return RedirectToAction("Me", "Profile");
        }
    }

    [HttpPost("ShiftInfo")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ShiftInfo(ShiftInfoViewModel model)
    {
        if (!ModelState.IsValid)
        {
            await PopulateCatalogAsync(model);
            return View(model);
        }

        try
        {
            var user = await GetCurrentUserInfoAsync();
            if (user is null)
                return NotFound();

            var existing = await shiftMgmt.GetShiftProfileAsync(user.Id);

            await shiftMgmt.UpdateShiftProfileAsync(new ShiftVolunteerProfileInfo(
                user.Id,
                ShiftInfoViewModel.MergeSkills(
                    model.SelectedSkills, model.SkillOtherText, existing?.Skills),
                ShiftInfoViewModel.MergePersistedQuirks(
                    model.TimePreference, model.SelectedQuirks, existing?.Quirks),
                ShiftInfoViewModel.MergeLanguages(
                    model.SelectedLanguages, model.LanguageOtherText, existing?.Languages)));
            await shiftProfiles.SetVolunteerTagPreferencesAsync(user.Id, model.EditableShiftTagIds);

            SetSuccess(localizer["Profile_Updated"].Value);
            return RedirectToAction(nameof(ShiftInfo));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save shift info for user");
            SetError(shiftsLocalizer["Shifts_ShiftProfile_SaveFailed"].Value);
            await PopulateCatalogAsync(model);
            return View(model);
        }
    }

    private async Task PopulateTagsAsync(ShiftInfoViewModel model, Guid userId)
    {
        await PopulateCatalogAsync(model);
        var summary = await shiftView.GetUserAsync(userId);
        model.EditableShiftTagIds = summary.TagPreferences.Select(t => t.ShiftTagId).ToList();
    }

    private async Task PopulateCatalogAsync(ShiftInfoViewModel model) =>
        model.AllShiftTags = (await shiftProfiles.GetTagsAsync())
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
