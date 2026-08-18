using Humans.Shifts.Contracts;
using Microsoft.AspNetCore.Mvc;
using Humans.Users.Contracts;

namespace Humans.Web.ViewComponents;

/// <summary>
/// Renders a red banner on /Shifts and /Shifts/Mine when the user has a
/// qualifying cantina signup but no dietary info on file. The visibility
/// gate lives inside the component so callers can invoke it unconditionally.
/// Spec: src/Sections/Humans.Users/Docs/features/dietary-medical-nudge.md (US-35.6)
/// </summary>
public sealed class DietaryMissingBannerViewComponent : ViewComponent
{
    private readonly IShiftManagementServiceRead _shiftMgmt;
    private readonly IUserServiceRead _userRead;
    private readonly ILogger<DietaryMissingBannerViewComponent> _logger;

    public DietaryMissingBannerViewComponent(
        IShiftManagementServiceRead shiftMgmt,
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
