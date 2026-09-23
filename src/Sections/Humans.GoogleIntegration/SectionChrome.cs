using Humans.Base.Interfaces;
using Humans.GoogleIntegration.ViewComponents;

namespace Humans.GoogleIntegration;

/// <summary>Layout chrome contribution — the widget gallery's sample of <c>&lt;vc:my-google-resources&gt;</c>.</summary>
internal sealed class SectionChrome : ISectionChrome
{
    public IEnumerable<ChromeComponent> Components() =>
        [new(ChromeSlots.WidgetGallery, typeof(MyGoogleResourcesViewComponent))];
}
