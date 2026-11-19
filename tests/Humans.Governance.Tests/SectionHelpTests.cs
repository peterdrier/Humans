using Humans.Base.Interfaces;
using AwesomeAssertions;
using Humans.Base.Models;

namespace Humans.Governance.Tests;

/// <summary>
/// Docs/help/ ships as an embedded resource: a renamed file or missing csproj entry shows as a null Guide or Glossary.
/// </summary>
public class SectionHelpTests
{
    private static SectionHelpEntry Entry(string key) =>
        ((ISectionHelp)new Section()).HelpEntries.Single(e => string.Equals(e.Key, key, StringComparison.Ordinal));

    [HumansFact]
    public void Contributes_its_help_entries_in_corpus_order()
    {
        ((ISectionHelp)new Section()).HelpEntries
            .Select(e => (e.Key, e.Order))
            .Should().Equal(("Governance", 60));
    }

    [HumansFact]
    public void Reads_the_governance_guide_and_glossary_out_of_the_assembly()
    {
        var entry = Entry("Governance");

        entry.Guide.Should().StartWith("## Governance & Tiers");
        entry.Guide.Should().EndWith("- Tier assignments expire after 2 years — you can reapply", "the file's trailing newline is framing, not content");
        entry.Glossary.Should().StartWith("## Governance Glossary");
        entry.Glossary.Should().Contain("| **Human** | A member of Nobodies Collective. We say \"humans\", not \"members\" or \"volunteers\". |");
    }

}
