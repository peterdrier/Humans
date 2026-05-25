using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Users;
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
    private readonly IUserServiceRead _userRead;
    private readonly ILogger<DietaryMissingBannerViewComponent> _logger;

    public DietaryMissingBannerViewComponent(
        IShiftManagementService shiftMgmt,
        IUserServiceRead userRead,
        ILogger<DietaryMissingBannerViewComponent> logger)
    {
        _shiftMgmt = shiftMgmt;
        _userRead = userRead;
        _logger = logger;
    }

    public async Task<IViewComponentResult> InvokeAsync(Guid userId)
    {
        try
        {
            var hasQualifyingSignup = await _shiftMgmt.HasQualifyingCantinaSignupAsync(userId);
            if (!hasQualifyingSignup) return Content(string.Empty);

            var profile = (await _userRead.GetUserInfoAsync(userId))?.Profile;
            if (!string.IsNullOrEmpty(profile?.DietaryPreference)) return Content(string.Empty);

            return View();
        }
        catch (Exception ex)
        {
            // A banner-fetch failure shouldn't crash /Shifts — log and render nothing.
            _logger.LogError(ex, "Failed to evaluate dietary banner for user {UserId}", userId);
            return Content(string.Empty);
        }
    }
}
