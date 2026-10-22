using AwesomeAssertions;
using Humans.Base.Models;

namespace Humans.Shifts.Tests;

/// <summary>
/// Docs/help/ ships as an embedded resource: a renamed file or missing csproj entry shows as a null Guide or Glossary.
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
            .Should().Equal(("Shifts", 40));
    }

    [HumansFact]
    public void Reads_the_shifts_guide_and_glossary_out_of_the_assembly()
    {
        var entry = Entry("Shifts");

        entry.Guide.Should().StartWith("## How Shifts Work");
        entry.Guide.Should().EndWith("- Reminder notifications are sent before your shift starts", "the file's trailing newline is framing, not content");
        entry.Glossary.Should().StartWith("## Shifts Glossary");
        entry.Glossary.Should().Contain("| **Human** | A member of Nobodies Collective. We say \"humans\", not \"members\" or \"volunteers\". |");
    }
}
