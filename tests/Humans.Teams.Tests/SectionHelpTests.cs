using Humans.Base.Interfaces;
using AwesomeAssertions;
using Humans.Base.Models;

namespace Humans.Teams.Tests;

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
            .Should().Equal(("Teams", 10));
    }

    [HumansFact]
    public void Reads_the_teams_guide_and_glossary_out_of_the_assembly()
    {
        var entry = Entry("Teams");

        entry.Guide.Should().StartWith("## How Teams Work");
        entry.Guide.Should().EndWith("Use the **Search** button to find humans across all teams by name or email. The **Roster** view shows all active humans in a flat list. The **Map** shows humans by location. **Birthdays** shows upcoming celebrations.", "the file's trailing newline is framing, not content");
        entry.Glossary.Should().StartWith("## Teams Glossary");
        entry.Glossary.Should().Contain("| **Human** | A member of Nobodies Collective. We say \"humans\", not \"members\" or \"volunteers\". |");
    }
}
