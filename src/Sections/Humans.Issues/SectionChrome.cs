using Humans.Base.Interfaces;

namespace Humans.Issues;

/// <summary>Layout chrome contribution — the Issues link in the signed-in user menu.</summary>
internal sealed class SectionChrome : ISectionChrome
{
    public IEnumerable<ChromeComponent> Components() =>
        [new(ChromeSlots.UserMenu, "IssuesUserMenu")];
}
