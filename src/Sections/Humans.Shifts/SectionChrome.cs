using Humans.Base.Interfaces;
using Humans.Shifts.ViewComponents;

namespace Humans.Shifts;

internal sealed class SectionChrome : ISectionChrome
{
    public IEnumerable<ChromeComponent> Components() =>
        [
            new(ChromeSlots.AdminDashboard, typeof(StaffingByDepartmentViewComponent), Weight: 10),
            new(ChromeSlots.WidgetGallery, typeof(ShiftsGalleryViewComponent)),
        ];
}
