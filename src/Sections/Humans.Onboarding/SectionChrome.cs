using Humans.Base.Interfaces;

namespace Humans.Onboarding;

/// <summary>Layout chrome contribution: the site-wide onboarding progress banner.</summary>
internal sealed class SectionChrome : ISectionChrome
{
    public IEnumerable<ChromeComponent> Components() =>
        [new(ChromeSlots.AboveContent, "OnboardingProgressBanner")];
}
