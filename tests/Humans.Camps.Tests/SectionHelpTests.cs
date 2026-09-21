using AwesomeAssertions;
using Humans.Base.Models;

namespace Humans.Camps.Tests;

/// <summary>
/// The help widget and the agent's glossary corpus read this section's help content through
/// the <c>ISectionHelp</c> seam, so what it contributes is pinned here rather than in Base.
/// The markdown itself lives in <c>Docs/help/</c> and ships as an embedded resource — a
/// renamed file or a missing csproj entry surfaces as a null Guide or Glossary below.
/// </summary>
public class SectionHelpTests
{
    private static SectionHelpEntry Entry(string key) =>
        new SectionHelp().HelpEntries.Single(e => string.Equals(e.Key, key, StringComparison.Ordinal));

    [HumansFact]
    public void Contributes_its_help_entries_in_corpus_order()
    {
        new SectionHelp().HelpEntries
            .Select(e => (e.Key, e.Order))
            .Should().Equal(("Camps", 50));
    }

    [HumansFact]
    public void Reads_the_camps_guide_and_glossary_out_of_the_assembly()
    {
        var entry = Entry("Camps");

        entry.Guide.Should().StartWith("## How Barrios Work");
        entry.Guide.Should().EndWith("- Barrio leads are notified when their barrio is approved or rejected", "the file's trailing newline is framing, not content");
        entry.Glossary.Should().StartWith("## Barrios Glossary");
        entry.Glossary.Should().Contain("| **Human** | A member of Nobodies Collective. We say \"humans\", not \"members\" or \"volunteers\". |");
    }
}
