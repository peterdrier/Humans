using Humans.Base.Interfaces;
using AwesomeAssertions;
using Humans.Base.Models;

namespace Humans.Camps.Tests;

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
            .Should().Equal(("Camps", 50));
    }

    [HumansFact]
    public void Help_ReadsCampsFromAssembly()
    {
        var entry = Entry("Camps");

        entry.Guide.Should().StartWith("## How Barrios Work");
        entry.Guide.Should().EndWith("- Humans are notified when they are assigned a role in a barrio", "the file's trailing newline is framing, not content");
        entry.Glossary.Should().StartWith("## Barrios Glossary");
        entry.Glossary.Should().Contain("| **Human** | A member of Nobodies Collective. We say \"humans\", not \"members\" or \"volunteers\". |");
    }
}
