using Humans.Base.Interfaces;
using Humans.AuditLog.ViewComponents;

namespace Humans.AuditLog;

internal sealed class SectionChrome : ISectionChrome
{
    public IEnumerable<ChromeComponent> Components() =>
        [
            new(ChromeSlots.AdminDashboard, typeof(AdminActivityCardViewComponent), Weight: 20),
            new(ChromeSlots.WidgetGallery, typeof(AuditLogGalleryViewComponent)),
        ];
}
