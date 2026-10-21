using Humans.Cantina.Contracts;
using Humans.Shifts.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Shifts.ViewComponents;

/// <summary>
/// Renders a gentle dietary-info prompt on /Shifts and /Shifts/Mine when the user has a
/// qualifying cantina signup and Cantina has neither a preference nor an "I don't want to answer"
/// on file. It never blocks a signup. The visibility gate lives inside the
/// component so callers can invoke it unconditionally.
/// Spec: src/Sections/Humans.Cantina/Docs/features/dietary-nudge.md
/// </summary>
public sealed class DietaryMissingBannerViewComponent : ViewComponent
{
    private readonly IShiftService _shiftMgmt;
    private readonly ICantinaService _cantina;
    private readonly ILogger<DietaryMissingBannerViewComponent> _logger;

    public DietaryMissingBannerViewComponent(
        IShiftService shiftMgmt,
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
            // The banner's copy speaks to a long shift, so it only shows with one booked.
            if (!await _shiftMgmt.HasQualifyingCantinaSignupAsync(userId, ct)) return Content(string.Empty);
            var dietary = await _cantina.GetDietaryProfileAsync(userId, ct);
            if (dietary is { HasPreference: true } or { DeclinedToAnswer: true }) return Content(string.Empty);

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
