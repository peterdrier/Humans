using Humans.Base.Interfaces;
using Humans.Base.Models;

namespace Humans.Governance;

/// <summary>
/// Section-help Guide and Glossary content for the Governance page, the Board dashboard and
/// the <c>/Admin</c> tool frame — was a row in
/// Base's deleted <c>SectionHelpContent</c> tables. The markdown itself lives beside this
/// section's other docs in <c>Docs/help/</c> and is embedded into this assembly.
/// </summary>
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
