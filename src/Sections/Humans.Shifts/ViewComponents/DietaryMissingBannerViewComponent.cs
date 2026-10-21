using Humans.Cantina.Contracts;
using Humans.Shifts.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Shifts.ViewComponents;

/// <summary>
/// Renders a red banner on /Shifts and /Shifts/Mine when the user has a
/// qualifying cantina signup but no dietary info on file. The visibility
/// gate lives inside the component so callers can invoke it unconditionally.
/// Spec: src/Sections/Humans.Cantina/Docs/features/dietary-nudge.md (US-35.6)
/// </summary>
public sealed class DietaryMissingBannerViewComponent : ViewComponent
{
    private readonly IShiftManagementService _shiftMgmt;
    private readonly ICantinaService _cantina;
    private readonly ILogger<DietaryMissingBannerViewComponent> _logger;

    public DietaryMissingBannerViewComponent(
        IShiftManagementService shiftMgmt,
        ICantinaService cantina,
        ILogger<DietaryMissingBannerViewComponent> logger)
    {
        _shiftMgmt = shiftMgmt;
        _cantina = cantina;
        _logger = logger;
    }

    public async Task<IViewComponentResult> InvokeAsync(Guid userId)
    {
        var ct = HttpContext.RequestAborted;
        try
        {
            var hasQualifyingSignup = await _shiftMgmt.HasQualifyingCantinaSignupAsync(userId, ct);
            if (!hasQualifyingSignup) return Content(string.Empty);

            if ((await _cantina.GetDietaryProfileAsync(userId, ct))?.HasPreference == true) return Content(string.Empty);

            return View();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A banner-fetch failure shouldn't crash /Shifts — log and render nothing.
            _logger.LogError(ex, "Failed to evaluate dietary banner for user {UserId}", userId);
            return Content(string.Empty);
        }
    }
}
