using Humans.Base.Interfaces;
using Humans.Events.ViewComponents;

namespace Humans.Events;

/// <summary>Events' sample cards on Debug's widget gallery.</summary>
internal sealed class SectionChrome : ISectionChrome
{
    public IEnumerable<ChromeComponent> Components() =>
        [new(ChromeSlots.WidgetGallery, typeof(EventsGalleryViewComponent))];
}
