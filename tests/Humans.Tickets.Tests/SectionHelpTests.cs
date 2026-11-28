using Humans.Base.Interfaces;
using AwesomeAssertions;
using Humans.Base.Models;

namespace Humans.Tickets.Tests;

/// <summary>
/// Docs/help/ ships as an embedded resource: a renamed file or missing csproj entry shows as a null Guide or Glossary.
/// </summary>
public class SectionHelpTests
{
    private static SectionHelpEntry Entry(string key) =>
        ((ISectionHelp)new Section()).HelpEntries.Single(e => string.Equals(e.Key, key, StringComparison.Ordinal));

    [HumansFact]
    public void Help_Entries_AreInCorpusOrder()
    {
        ((ISectionHelp)new Section()).HelpEntries
            .Select(e => (e.Key, e.Order))
            .Should().Equal(("Tickets", 90));
    }

    [HumansFact]
    public void Help_ReadsTicketsGuideFromAssembly()
    {
        var entry = Entry("Tickets");

        entry.Guide.Should().StartWith("## Tickets");
        entry.Guide.Should().EndWith("- Ticket data syncs from TicketTailor on a regular schedule", "the file's trailing newline is framing, not content");
        entry.Glossary.Should().StartWith("## Tickets Glossary");
        entry.Glossary.Should().Contain("| **Human** | A member of Nobodies Collective. We say \"humans\", not \"members\" or \"volunteers\". |");
    }
}
