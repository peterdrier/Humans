using Humans.Base.Interfaces;
using Humans.Base.Models;

namespace Humans.Onboarding;

/// <summary>The markdown lives in <c>Docs/help/</c> and is embedded into this assembly.</summary>
internal sealed class SectionHelp : ISectionHelp
{
    public IReadOnlyList<SectionHelpEntry> HelpEntries => Entries;

    private static readonly IReadOnlyList<SectionHelpEntry> Entries =
    [
        SectionHelpEntry.FromEmbeddedMarkdown(typeof(SectionHelp).Assembly, "OnboardingReview", 70),
    ];
}
