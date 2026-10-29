using Humans.Base.Interfaces;
using Humans.Calendar.ViewComponents;

namespace Humans.Calendar;

/// <summary>Calendar's sample card on Debug's widget gallery.</summary>
internal sealed class SectionChrome : ISectionChrome
{
    public IEnumerable<ChromeComponent> Components() =>
        [new(ChromeSlots.WidgetGallery, typeof(CalendarGalleryViewComponent))];
}
