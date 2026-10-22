using Humans.Base.Interfaces;
using Humans.Base.Models;

namespace Humans.Tickets;

internal sealed class SectionHelp : ISectionHelp
{
    public IReadOnlyList<SectionHelpEntry> HelpEntries => Entries;

    private static readonly IReadOnlyList<SectionHelpEntry> Entries =
    [
        SectionHelpEntry.FromEmbeddedMarkdown(typeof(SectionHelp).Assembly, "Tickets", 90),
    ];
}
