using Humans.Base.Interfaces;
using Humans.Notifications.ViewComponents;

namespace Humans.Notifications;

internal sealed class SectionChrome : ISectionChrome
{
    public IEnumerable<ChromeComponent> Components() =>
        [
            new(ChromeSlots.HeaderRight, typeof(NotificationBellViewComponent)),
        ];
}
