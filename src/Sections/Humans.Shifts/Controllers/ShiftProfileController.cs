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

/// <summary>
/// The volunteer's own shift-matching profile — skills, quirks and languages — at
/// <c>/Shifts/ShiftInfo</c>.
/// </summary>
/// <remarks>
/// Both actions read and write <c>volunteer_event_profiles</c>, a Shifts table.
/// </remarks>
[Authorize]
[Route("Shifts")]
internal sealed class ShiftProfileController(
    IShiftManagement shiftMgmt,
    IShiftVolunteerProfiles shiftProfiles,
    IShiftView shiftView,
    IUserService userService,
    // SharedResource: the only string this controller resolves is Profile_Updated,
    // which belongs to Shell's profile vocabulary.
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

            var shiftProfile = await shiftMgmt.GetOrCreateShiftProfileAsync(user.Id);

            shiftProfile.Skills = ShiftInfoViewModel.MergeSkills(
                model.SelectedSkills, model.SkillOtherText, shiftProfile.Skills);
            shiftProfile.Quirks = ShiftInfoViewModel.MergePersistedQuirks(
                model.TimePreference, model.SelectedQuirks, shiftProfile.Quirks);
            shiftProfile.Languages = ShiftInfoViewModel.MergeLanguages(
                model.SelectedLanguages, model.LanguageOtherText, shiftProfile.Languages);

            await shiftMgmt.UpdateShiftProfileAsync(shiftProfile);
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
