using Humans.Base.Interfaces;
using Humans.Camps.ViewComponents;

namespace Humans.Camps;

/// <summary>Camps' sample cards on Debug's widget gallery.</summary>
internal sealed class SectionChrome : ISectionChrome
{
    public IEnumerable<ChromeComponent> Components() =>
        [new(ChromeSlots.WidgetGallery, typeof(CampsGalleryViewComponent))];
}
