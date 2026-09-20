using Humans.Base.Interfaces;
using Humans.Base.Models;

namespace Humans.Shifts;

/// <summary>
/// Section-help Guide and Glossary content for the Shifts pages — was a row in
/// Base's deleted <c>SectionHelpContent</c> tables. The markdown itself lives beside this
/// section's other docs in <c>Docs/help/</c> and is embedded into this assembly.
/// </summary>
internal sealed class SectionHelp : ISectionHelp
{
    public IReadOnlyList<SectionHelpEntry> HelpEntries => Entries;

    // Read once per process: the markdown ships inside this assembly and never changes at runtime.
    private static readonly IReadOnlyList<SectionHelpEntry> Entries =
    [
        SectionHelpEntry.FromEmbeddedMarkdown(typeof(SectionHelp).Assembly, "Shifts", 40),
    ];
}
