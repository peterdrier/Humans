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
/// <c>/Profile/Me/ShiftInfo</c>.
/// </summary>
/// <remarks>
/// Both actions read and write <c>volunteer_event_profiles</c>, a Shifts table, but keep
/// the <c>[Route("Profile")]</c> prefix so the member-facing URLs do not change.
/// </remarks>
[Authorize]
[Route("Profile")]
internal sealed class ShiftProfileController(
    IShiftManagementService shiftMgmt,
    IUserServiceRead userService,
    // SharedResource: the only string this controller resolves is Profile_Updated,
    // which belongs to Shell's profile vocabulary.
    IStringLocalizer<SharedResource> localizer,
    ILogger<ShiftProfileController> logger) : HumansControllerBase(userService)
{
    [HttpGet("Me/ShiftInfo")]
    public async Task<IActionResult> ShiftInfo()
    {
        try
        {
            var user = await GetCurrentUserInfoAsync();
            if (user is null)
                return NotFound();
            var profile = await shiftMgmt.GetShiftProfileAsync(user.Id);
            return View(ShiftInfoViewModel.FromProfile(profile));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load shift info for user");
            SetError("Failed to load shift info.");
            return RedirectToAction("Me", "Profile");
        }
    }

    [HttpPost("Me/ShiftInfo")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ShiftInfo(ShiftInfoViewModel model)
    {
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

            SetSuccess(localizer["Profile_Updated"].Value);
            return RedirectToAction(nameof(ShiftInfo));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save shift info for user");
            SetError("Failed to save shift info.");
            return View(model);
        }
    }
}
