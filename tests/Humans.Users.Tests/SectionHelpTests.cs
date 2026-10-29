using Humans.Base.Interfaces;
using AwesomeAssertions;
using Humans.Base.Models;

namespace Humans.Users.Tests;

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
            .Should().Equal(("Profile", 20));
    }

    [HumansFact]
    public void Reads_the_profile_guide_and_glossary_out_of_the_assembly()
    {
        var entry = Entry("Profile");

        entry.Guide.Should().StartWith("## Your Profile");
        entry.Guide.Should().EndWith("- Your Google Workspace access is provisioned based on your team memberships", "the file's trailing newline is framing, not content");
        entry.Glossary.Should().StartWith("## Profile Glossary");
        entry.Glossary.Should().Contain("| **Human** | A member of Nobodies Collective. We say \"humans\", not \"members\" or \"volunteers\". |");
    }
}
