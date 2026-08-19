using Microsoft.AspNetCore.Mvc;

namespace Humans.Shifts.Contracts;

/// <summary>
/// The "Staffing by department" card — event-wide coverage plus the per-department
/// breakdown. Rendered as <c>&lt;vc:staffing-by-department&gt;</c> from Shell's admin
/// dashboard.
/// </summary>
/// <remarks>
/// Public, under <c>Contracts/</c>, like <see cref="ShiftSignupsViewComponent"/> — MVC's
/// default provider discovers it and the tag helper is generated at compile time.
/// The department breakdown has no source yet; the card says so and points at Shifts.
/// </remarks>
public sealed class StaffingByDepartmentViewComponent(IShiftManagementServiceRead shifts) : ViewComponent
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
