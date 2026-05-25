using Humans.Application.Interfaces.Shifts;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.ViewComponents;

/// <summary>
/// Renders a red banner on /Shifts and /Shifts/Mine when the user has a
/// qualifying cantina signup but no dietary info on file. The visibility
/// gate lives inside the component so callers can invoke it unconditionally.
/// Spec: docs/superpowers/specs/2026-05-25-dietary-prompt-tightening-design.md
/// </summary>
public sealed class DietaryMissingBannerViewComponent : ViewComponent
{
    private readonly IShiftManagementService _shiftMgmt;

    public DietaryMissingBannerViewComponent(IShiftManagementService shiftMgmt)
    {
        _shiftMgmt = shiftMgmt;
    }

    public async Task<IViewComponentResult> InvokeAsync(Guid userId)
    {
        var hasQualifyingSignup = await _shiftMgmt.HasQualifyingCantinaSignupAsync(userId);
        if (!hasQualifyingSignup) return Content(string.Empty);

        var profile = await _shiftMgmt.GetShiftProfileAsync(userId, includeMedical: false);
        if (!string.IsNullOrEmpty(profile?.DietaryPreference)) return Content(string.Empty);

        return View();
    }
}
