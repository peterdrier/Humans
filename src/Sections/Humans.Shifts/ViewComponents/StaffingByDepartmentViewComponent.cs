using Humans.Shifts.Services;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Shifts.ViewComponents;

/// <summary>
/// Internal: invoked by type through the chrome slot, not a tag helper. Shows overall coverage and a placeholder
/// for the department breakdown, which has no source yet.
/// </summary>
internal sealed class StaffingByDepartmentViewComponent(IShiftManagement shifts) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        var (filled, total, _) = await shifts.GetOverallCoverageAsync(HttpContext.RequestAborted);
        return View(new StaffingByDepartmentViewModel(
            Filled: total > 0 ? filled : null,
            Total: total > 0 ? total : null));
    }
}

internal sealed record StaffingByDepartmentViewModel(int? Filled, int? Total);
