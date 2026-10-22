using Humans.Base.Interfaces;
using Humans.Issues.ViewComponents;

namespace Humans.Issues;

internal sealed class SectionChrome : ISectionChrome
{
    public IEnumerable<ChromeComponent> Components() =>
        [new(ChromeSlots.UserMenu, typeof(IssuesUserMenuViewComponent))];
}
