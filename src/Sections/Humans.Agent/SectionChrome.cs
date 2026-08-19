using Humans.Base.Interfaces;

namespace Humans.Agent;

/// <summary>Layout chrome contribution — was the by-name HelpWidget invocation at the end of
/// Shell's <c>_Layout.cshtml</c> and <c>_AdminLayout.cshtml</c> bodies.</summary>
internal sealed class SectionChrome : ISectionChrome
{
    public IEnumerable<ChromeComponent> Components() =>
        [new(ChromeSlots.BodyEnd, "HelpWidget")];
}
