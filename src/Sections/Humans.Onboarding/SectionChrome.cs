using Humans.Base.Interfaces;

namespace Humans.Onboarding;

/// <summary>Layout chrome contribution — was the above-content invocation in Shell's <c>_Layout.cshtml</c>.</summary>
internal sealed class SectionChrome : ISectionChrome
{
    public IEnumerable<ChromeComponent> Components() =>
        [new(ChromeSlots.AboveContent, "OnboardingProgressBanner")];
}
