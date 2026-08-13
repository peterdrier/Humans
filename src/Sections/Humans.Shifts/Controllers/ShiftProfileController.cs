using Humans.Application.Interfaces.Users;
using Humans.Shifts.Models;
using Humans.Shifts.Services;
using Humans.UI;
using Humans.UI.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

namespace Humans.Shifts.Controllers;

/// <summary>
/// The volunteer's own shift-matching profile — skills, quirks and languages — at
/// <c>/Profile/Me/ShiftInfo</c>.
/// </summary>
/// <remarks>
/// Two actions carved off Shell's <c>ProfileController</c> at the section's G5
/// (nobodies-collective/Humans#866): both read and write <c>volunteer_event_profiles</c>,
/// a Shifts table, and touch nothing of Profiles'. <c>FinanceController</c>'s case
/// exactly — one section's actions sitting under another's route prefix.
/// <c>[Route("Profile")]</c> stays on both halves so neither URL changes.
/// </remarks>
[Authorize]
[Route("Profile")]
internal sealed class ShiftProfileController(
    IShiftManagementService shiftMgmt,
    IUserService userService,
    // SharedResource, not ShiftsResource: the one string this controller resolves is
    // Profile_Updated, which belongs to Shell's profile vocabulary and stayed there. The
    // page's own 13 ShiftInfo_ keys are the view's, and the view reads ShiftsResource
    // through _ViewImports. Governance's "a section may bind two markers" case.
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
