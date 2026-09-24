using Humans.Base.Interfaces;
using Humans.Shifts.ViewComponents;

namespace Humans.Shifts;

/// <summary>Layout chrome contribution — the staffing-by-department card on the admin dashboard.</summary>
internal sealed class SectionChrome : ISectionChrome
{
    public IEnumerable<ChromeComponent> Components() =>
        [
            new(ChromeSlots.AdminDashboard, typeof(StaffingByDepartmentViewComponent), Weight: 10),
            new(ChromeSlots.WidgetGallery, typeof(ShiftsGalleryViewComponent)),
        ];
}
