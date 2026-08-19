using Humans.Base.Interfaces;

namespace Humans.Notifications;

/// <summary>Layout chrome contribution — was the header-right invocation in Shell's layouts.</summary>
internal sealed class SectionChrome : ISectionChrome
{
    public IEnumerable<ChromeComponent> Components() =>
        [new(ChromeSlots.HeaderRight, "NotificationBell")];
}
