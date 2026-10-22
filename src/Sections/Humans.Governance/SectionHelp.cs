using Humans.Base.Interfaces;
using Humans.Base.Models;

namespace Humans.Governance;

internal sealed class SectionHelp : ISectionHelp
{
    public IReadOnlyList<SectionHelpEntry> HelpEntries => Entries;

    // Read once per process: the markdown ships inside this assembly and never changes at runtime.
    private static readonly IReadOnlyList<SectionHelpEntry> Entries =
    [
        SectionHelpEntry.FromEmbeddedMarkdown(typeof(SectionHelp).Assembly, "Admin", 30),
        SectionHelpEntry.FromEmbeddedMarkdown(typeof(SectionHelp).Assembly, "Governance", 60),
        SectionHelpEntry.FromEmbeddedMarkdown(typeof(SectionHelp).Assembly, "Board", 80),
    ];
}
