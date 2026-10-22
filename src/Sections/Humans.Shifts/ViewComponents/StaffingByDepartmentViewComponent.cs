using Humans.Shifts.Services;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Shifts.ViewComponents;

/// <summary>
/// Internal: invoked by type through the chrome slot, not a tag helper. The department breakdown has no source
/// yet, so <c>Departments</c> is always empty and the card shows its "no data yet" copy.
/// </summary>
internal sealed class StaffingByDepartmentViewComponent(IShiftManagement shifts) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        var (filled, total, _) = await shifts.GetOverallCoverageAsync(HttpContext.RequestAborted);
        return View(new StaffingByDepartmentViewModel(
            Filled: total > 0 ? filled : null,
            Total: total > 0 ? total : null,
            Departments: []));
    }
}

internal sealed record StaffingByDepartmentViewModel(
    int? Filled,
    int? Total,
    IReadOnlyList<DepartmentCoverage> Departments);

internal sealed record DepartmentCoverage(string Name, int Filled, int Total)
{
    public double Ratio => Total > 0 ? (double)Filled / Total : 0;
    public string TrackClass => Ratio >= 0.7 ? "" : Ratio >= 0.5 ? "low" : "crit";
}
