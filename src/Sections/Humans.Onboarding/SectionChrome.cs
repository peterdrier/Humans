using Humans.Base.Interfaces;
using Humans.Onboarding.ViewComponents;

namespace Humans.Onboarding;

internal sealed class SectionChrome : ISectionChrome
{
    public IEnumerable<ChromeComponent> Components() =>
        [new(ChromeSlots.AboveContent, typeof(OnboardingProgressBannerViewComponent))];
}
