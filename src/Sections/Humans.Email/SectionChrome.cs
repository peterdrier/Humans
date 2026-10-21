using Humans.Base.Interfaces;
using Humans.Email.ViewComponents;

namespace Humans.Email;

internal sealed class SectionChrome : ISectionChrome
{
    public IEnumerable<ChromeComponent> Components() =>
        [
            new(ChromeSlots.WidgetGallery, typeof(EmailGalleryViewComponent)),
        ];
}
