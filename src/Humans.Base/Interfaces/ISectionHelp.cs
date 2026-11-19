using Humans.Base.Models;

namespace Humans.Base.Interfaces;

/// <summary>
/// The section-help content a section owns — the Guide and Glossary markdown behind
/// <c>&lt;vc:access-matrix section="..."&gt;</c>'s modal and the agent's preloaded glossary
/// corpus. Sections and Shell contribute the entries for the pages they own and the two consumers
/// inject <c>IEnumerable&lt;ISectionHelp&gt;</c>, so neither Base nor Shell holds a list of
/// which sections have help content. One owner may contribute several entries; the flat corpus order comes from
/// <see cref="SectionHelpEntry.Order"/>, not from registration order. The markdown itself
/// lives beside the section's other docs in <c>Docs/help/</c> and ships as an embedded
/// resource — see <see cref="SectionHelpEntry.FromEmbeddedMarkdown"/>.
/// </summary>
public interface ISectionHelp : ISectionContribution
{
    IReadOnlyList<SectionHelpEntry> HelpEntries { get; }
}
