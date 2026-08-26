using Humans.Base.Interfaces;

namespace Humans.Notifications;

/// <summary>Renders the notification bell in the header's right chrome slot.</summary>
internal sealed class SectionChrome : ISectionChrome
{
    public IEnumerable<ChromeComponent> Components() =>
        [new(ChromeSlots.HeaderRight, "NotificationBell")];
}
