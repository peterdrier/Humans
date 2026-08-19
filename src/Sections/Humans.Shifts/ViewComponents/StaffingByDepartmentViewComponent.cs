using Humans.Shifts.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Shifts.ViewComponents;

/// <summary>
/// The "Staffing by department" card — event-wide coverage plus the per-department
/// breakdown. Contributed into the admin dashboard's <c>admin-dashboard</c> chrome slot
/// (<see cref="Humans.Shifts.SectionChrome"/>) since nobodies-collective/Humans#1091.
/// </summary>
/// <remarks>
/// Internal — invoked by name through <c>ChromeSlotViewComponent</c>'s
/// <c>Component.InvokeAsync</c>, not a <c>&lt;vc:&gt;</c> tag helper, so it does not need to
/// be public under <c>Contracts/</c>. The department breakdown has no source yet; the card
/// says so and points at Shifts.
/// </remarks>
internal sealed class StaffingByDepartmentViewComponent(IShiftManagementServiceRead shifts) : ViewComponent
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
