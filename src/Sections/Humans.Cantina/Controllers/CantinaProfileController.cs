using Humans.Base.Authorization;
using Humans.Base.Controllers;
using Humans.Cantina.Models;
using Humans.Cantina.Services;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

namespace Humans.Cantina.Controllers;

/// <summary>
/// Owner-only: every action acts on the signed-in member. Dietary info is opt-in and never gates a shift signup.
/// Medical conditions live in Users at <c>/Profile/Me/Medical</c>.
/// </summary>
[Authorize]
[Route("Cantina")]
internal sealed class CantinaProfileController(
    IDietaryProfileManagement dietary,
    IUserService userService,
    IStringLocalizer<CantinaResource> localizer,
    ILogger<CantinaProfileController> logger) : HumansControllerBase(userService)
{
    [HttpGet("Dietary")]
    public async Task<IActionResult> Dietary(string? returnAction = null)
    {
        var ct = HttpContext.RequestAborted;
        try
        {
            var user = await GetCurrentUserInfoAsync(ct);
            if (user is null)
                return NotFound();

            var vm = DietaryViewModel.FromProfile(await dietary.GetDietaryProfileAsync(user.Id, ct));
            vm.ReturnAction = returnAction;
            return View(vm);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Failed to load dietary info for user");
            SetError(localizer["Cantina_Dietary_LoadFailed"].Value);
            return RedirectToAction("Me", "Profile");
        }
    }

    [HttpPost("Dietary")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Dietary(DietaryViewModel model)
    {
        if (model.DeclineToAnswer)
            return await DeclineAsync(model.ReturnAction);

        if (!ModelState.IsValid)
            return View(model);

        // Field-targeted and localized; the service refuses the same case as a backstop.
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

            SetSuccess(localizer["Cantina_Dietary_Saved"].Value);
            return ReturnTo(model.ReturnAction);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save dietary info");
            SetError(localizer["Cantina_Dietary_SaveFailed"].Value);
            return View(model);
        }
    }

    // Skips answer validation: nothing posted besides the choice is kept.
    private async Task<IActionResult> DeclineAsync(string? returnAction)
    {
        try
        {
            var user = await GetCurrentUserInfoAsync();
            if (user is null)
                return NotFound();

            await dietary.DeclineAsync(user.Id);
            SetSuccess(localizer["Cantina_Dietary_Declined"].Value);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to record dietary decline");
            SetError(localizer["Cantina_Dietary_SaveFailed"].Value);
        }
        return ReturnTo(returnAction);
    }

    private RedirectToActionResult ReturnTo(string? returnAction) =>
        string.Equals(returnAction, "shifts", StringComparison.Ordinal)
            ? RedirectToAction("Index", "Shifts")
            : RedirectToAction("Index", "Home");
}
