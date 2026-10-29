using Humans.Base.Interfaces;
using Humans.Teams.ViewComponents;

namespace Humans.Teams;

/// <summary>Teams' sample cards on Debug's widget gallery.</summary>
internal sealed class SectionChrome : ISectionChrome
{
    public IEnumerable<ChromeComponent> Components() =>
        [new(ChromeSlots.WidgetGallery, typeof(TeamsGalleryViewComponent))];
}
