using Humans.Base.Interfaces;
using Humans.Agent.ViewComponents;

namespace Humans.Agent;

internal sealed class SectionChrome : ISectionChrome
{
    public IEnumerable<ChromeComponent> Components() =>
        [
            new(ChromeSlots.BodyEnd, typeof(HelpWidgetViewComponent)),
            new(ChromeSlots.WidgetGallery, typeof(AgentGalleryViewComponent)),
        ];
}
