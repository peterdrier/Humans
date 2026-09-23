using Humans.Base.Interfaces;
using Humans.Issues.ViewComponents;

namespace Humans.Issues;

/// <summary>Layout chrome contribution — the Issues link in the signed-in user menu.</summary>
internal sealed class SectionChrome : ISectionChrome
{
    public IEnumerable<ChromeComponent> Components() =>
        [new(ChromeSlots.UserMenu, typeof(IssuesUserMenuViewComponent))];
}
