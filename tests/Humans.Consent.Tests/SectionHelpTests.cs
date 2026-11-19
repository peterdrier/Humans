using Humans.Base.Interfaces;
using AwesomeAssertions;
using Humans.Base.Models;

namespace Humans.Consent.Tests;

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
            .Should().Equal(("ConsentReview", 70), ("LegalDocuments", 83));
    }

    [HumansFact]
    public void Reads_the_consentreview_guide_and_glossary_out_of_the_assembly()
    {
        var entry = Entry("ConsentReview");

        entry.Guide.Should().StartWith("## Consent Review");
        entry.Guide.Should().EndWith("- The review queue badge in the nav updates in real-time", "the file's trailing newline is framing, not content");
        entry.Glossary.Should().StartWith("## Consent Review Glossary");
        entry.Glossary.Should().Contain("| **Human** | A member of Nobodies Collective. We say \"humans\", not \"members\" or \"volunteers\". |");
    }

    [HumansFact]
    public void Reads_the_LegalDocuments_help_out_of_the_assembly()
    {
        var entry = ((ISectionHelp)new Section()).HelpEntries
            .Single(e => string.Equals(e.Key, "LegalDocuments", StringComparison.Ordinal));

        entry.Guide.Should().StartWith("## Legal Documents");
        entry.Glossary.Should().StartWith("## Legal Documents Glossary");
    }
}
