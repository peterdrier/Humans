using System.Globalization;
using Humans.Base.Authorization;
using Humans.Base.Controllers;
using Humans.Cantina.Models;
using Humans.Cantina.Services;
using Humans.Shifts.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

namespace Humans.Cantina.Controllers;

/// <summary>
/// The member's own dietary profile at <c>/Profile/Me/Dietary</c>, and the replay of the shift
/// signup the dietary gate interrupted (Docs/features/dietary-nudge.md, US-35.6).
/// </summary>
/// <remarks>
/// Reads and writes <c>cantina_dietary_profiles</c>, a Cantina table, under the
/// <c>[Route("Profile")]</c> prefix so the member's own pages stay together (Shifts'
/// <c>ShiftProfileController</c> is the precedent). Owner-only: every action acts on the
/// signed-in member. Medical conditions are not here; Users serves them at
/// <c>/Profile/Me/Medical</c>.
/// </remarks>
[Authorize]
[Route("Profile")]
internal sealed class CantinaProfileController(
    IDietaryProfileManagement dietary,
    IShiftSignups shiftSignups,
    IUserService userService,
    IStringLocalizer<CantinaResource> localizer,
    ILogger<CantinaProfileController> logger) : HumansControllerBase(userService)
{
    /// <summary>The page's address until Users decomposition commit 10, kept for bookmarks.</summary>
    [HttpGet("Me/DietaryMedical")]
    public IActionResult LegacyDietaryMedical(
        string? returnAction = null,
        Guid? shiftId = null,
        Guid? rotaId = null,
        int? startDayOffset = null,
        int? endDayOffset = null) =>
        RedirectToActionPermanent(nameof(Dietary), new { returnAction, shiftId, rotaId, startDayOffset, endDayOffset });

    [HttpGet("Me/Dietary")]
    public async Task<IActionResult> Dietary(
        string? returnAction = null,
        Guid? shiftId = null,
        Guid? rotaId = null,
        int? startDayOffset = null,
        int? endDayOffset = null)
    {
        var ct = HttpContext.RequestAborted;
        try
        {
            var user = await GetCurrentUserInfoAsync(ct);
            if (user is null)
                return NotFound();

            var vm = DietaryViewModel.FromProfile(await dietary.GetDietaryProfileAsync(user.Id, ct));
            vm.ReturnAction = returnAction;
            vm.ShiftId = shiftId;
            vm.RotaId = rotaId;
            vm.StartDayOffset = startDayOffset;
            vm.EndDayOffset = endDayOffset;
            return View(vm);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Failed to load dietary info for user");
            SetError(localizer["Cantina_Dietary_LoadFailed"].Value);
            return RedirectToAction("Me", "Profile");
        }
    }

    [HttpPost("Me/Dietary")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Dietary(DietaryViewModel model)
    {
        if (!ModelState.IsValid)
            return View(model);

        // Field-targeted, localized primary path for "Other text required iff Other selected";
        // the service refuses the same case as a backstop.
        if (model.OtherAllergyWithoutText)
        {
            ModelState.AddModelError(nameof(model.AllergyOtherText), localizer["Cantina_Dietary_AllergyOther_Required"].Value);
            return View(model);
        }
        if (model.OtherIntoleranceWithoutText)
        {
            ModelState.AddModelError(nameof(model.IntoleranceOtherText), localizer["Cantina_Dietary_IntoleranceOther_Required"].Value);
            return View(model);
        }

        try
        {
            var user = await GetCurrentUserInfoAsync();
            if (user is null)
                return NotFound();

            var errorKey = await dietary.SaveAsync(user.Id, model.ToAnswers(user.Id));
            if (errorKey is not null)
            {
                ModelState.AddModelError(string.Empty, localizer[errorKey].Value);
                return View(model);
            }

            // The member was bounced here by the dietary gate on ShiftsController.ToggleDay: re-run
            // the original signup and land them on /Shifts. A failed replay does not undo the save;
            // the member can retry the signup from /Shifts without re-entering anything.
            return await ReplayShiftSignupAsync(user.Id, model);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save dietary info");
            SetError(localizer["Cantina_Dietary_SaveFailed"].Value);
            return View(model);
        }
    }

    // Flash mapping duplicated from ShiftsController.SignUp/SignUpRange; sharing it is tracked separately.
    private async Task<IActionResult> ReplayShiftSignupAsync(Guid userId, DietaryViewModel model)
    {
        switch (model.ReturnAction)
        {
            case "signup" when model.ShiftId is { } sid:
                {
                    var privileged = ShiftRoleChecks.IsPrivilegedSignupApprover(User);
                    var result = await shiftSignups.SignUpAsync(
                        userId,
                        sid,
                        actorUserId: null,
                        flags: privileged ? ShiftSignupRequestFlags.Privileged : ShiftSignupRequestFlags.None);
                    if (!result.Success)
                        SetError(result.Error ?? localizer["Cantina_Dietary_ShiftSignupFailed"].Value);
                    else
                        SetSuccess(result.Warning is not null
                            ? string.Format(CultureInfo.CurrentCulture,
                                localizer["Cantina_Dietary_ShiftSignupSucceededWithWarning"].Value, result.Warning)
                            : localizer["Cantina_Dietary_ShiftSignupSucceeded"].Value);
                    return RedirectToAction("Index", "Shifts");
                }
            case "signuprange" when model.RotaId is { } rid
                                     && model.StartDayOffset is { } sd
                                     && model.EndDayOffset is { } ed:
                {
                    var privileged = ShiftRoleChecks.IsPrivilegedSignupApprover(User);
                    var flags = ShiftSignupRequestFlags.SkipConflicts;
                    if (privileged) flags |= ShiftSignupRequestFlags.Privileged;
                    var result = await shiftSignups.SignUpRangeAsync(
                        userId,
                        rid,
                        sd,
                        ed,
                        actorUserId: null,
                        flags: flags);
                    if (!result.Success)
                        SetError(result.Error ?? localizer["Cantina_Dietary_ShiftRangeSignupFailed"].Value);
                    else
                        SetSuccess(result.Warning is not null
                            ? string.Format(CultureInfo.CurrentCulture,
                                localizer["Cantina_Dietary_ShiftRangeSignupSucceededWithWarning"].Value, result.Warning)
                            : localizer["Cantina_Dietary_ShiftRangeSignupSucceeded"].Value);
                    return RedirectToAction("Index", "Shifts");
                }
            case "shifts":
                SetSuccess(localizer["Cantina_Dietary_Saved"].Value);
                return RedirectToAction("Index", "Shifts");
            default:
                SetSuccess(localizer["Cantina_Dietary_Saved"].Value);
                return RedirectToAction("Index", "Home");
        }
    }
}
