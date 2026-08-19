using Humans.Base.Interfaces;

namespace Humans.Shifts;

/// <summary>Layout chrome contribution — the staffing-by-department card on the admin dashboard.</summary>
internal sealed class SectionChrome : ISectionChrome
{
    public IEnumerable<ChromeComponent> Components() =>
        [new(ChromeSlots.AdminDashboard, "StaffingByDepartment", Weight: 10)];
}
