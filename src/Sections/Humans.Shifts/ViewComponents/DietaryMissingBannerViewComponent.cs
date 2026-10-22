using Humans.Cantina.Contracts;
using Humans.Shifts.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Shifts.ViewComponents;

/// <summary>
/// Never blocks a signup. The visibility gate lives inside, so callers invoke it unconditionally. Spec:
/// <c>src/Sections/Humans.Cantina/Docs/features/dietary-nudge.md</c>.
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
            // A banner failure must not crash /Shifts.
            _logger.LogError(ex, "Failed to evaluate dietary banner for user {UserId}", userId);
            return Content(string.Empty);
        }
    }
}
