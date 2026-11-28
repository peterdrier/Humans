using Humans.Base.Interfaces;
using AwesomeAssertions;
using Humans.Base.Models;

namespace Humans.Shifts.Tests;

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
            .Should().Equal(("Shifts", 40));
    }

    [HumansFact]
    public void Help_ReadsGuideFromAssembly()
    {
        var entry = Entry("Shifts");

        entry.Guide.Should().StartWith("## How Shifts Work");
        entry.Guide.Should().EndWith("- Reminder notifications are sent before your shift starts", "the file's trailing newline is framing, not content");
        entry.Glossary.Should().StartWith("## Shifts Glossary");
        entry.Glossary.Should().Contain("| **Human** | A member of Nobodies Collective. We say \"humans\", not \"members\" or \"volunteers\". |");
    }
}
