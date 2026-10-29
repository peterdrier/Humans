using Humans.Base.Interfaces;
using Humans.Tickets.ViewComponents;

namespace Humans.Tickets;

/// <summary>Tickets' sample cards on Debug's widget gallery.</summary>
internal sealed class SectionChrome : ISectionChrome
{
    public IEnumerable<ChromeComponent> Components() =>
        [new(ChromeSlots.WidgetGallery, typeof(TicketsGalleryViewComponent))];
}
