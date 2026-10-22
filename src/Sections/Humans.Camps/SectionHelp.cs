using Humans.Base.Models;

namespace Humans.Camps;

/// <summary>The markdown lives in <c>Docs/help/</c> and is embedded into this assembly.</summary>
internal sealed class SectionHelp : ISectionHelp
{
    public IReadOnlyList<SectionHelpEntry> HelpEntries => Entries;

    // Read once per process: the markdown ships inside this assembly and never changes at runtime.
    private static readonly IReadOnlyList<SectionHelpEntry> Entries =
    [
        SectionHelpEntry.FromEmbeddedMarkdown(typeof(SectionHelp).Assembly, "Camps", 50),
    ];
}
