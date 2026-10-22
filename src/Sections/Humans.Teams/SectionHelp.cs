using Humans.Base.Interfaces;
using Humans.Base.Models;

namespace Humans.Teams;

internal sealed class SectionHelp : ISectionHelp
{
    public IReadOnlyList<SectionHelpEntry> HelpEntries => Entries;

    // Read once: the markdown is embedded in this assembly and never changes at runtime.
    private static readonly IReadOnlyList<SectionHelpEntry> Entries =
    [
        SectionHelpEntry.FromEmbeddedMarkdown(typeof(SectionHelp).Assembly, "Teams", 10),
    ];
}
