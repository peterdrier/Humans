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
    public void Help_Entries_AreInCorpusOrder()
    {
        ((ISectionHelp)new Section()).HelpEntries
            .Select(e => (e.Key, e.Order))
            .Should().Equal(("ConsentReview", 70), ("LegalDocuments", 83));
    }

    [HumansFact]
    public void Help_ReadsConsentReviewFromAssembly()
    {
        var entry = Entry("ConsentReview");

        entry.Guide.Should().StartWith("## Consent Review");
        entry.Guide.Should().EndWith("- The review queue badge in the nav updates in real-time", "the file's trailing newline is framing, not content");
        entry.Glossary.Should().StartWith("## Consent Review Glossary");
        entry.Glossary.Should().Contain("| **Human** | A member of Nobodies Collective. We say \"humans\", not \"members\" or \"volunteers\". |");
    }

    [HumansFact]
    public void Help_ReadsLegalDocumentsFromAssembly()
    {
        var entry = ((ISectionHelp)new Section()).HelpEntries
            .Single(e => string.Equals(e.Key, "LegalDocuments", StringComparison.Ordinal));

        entry.Guide.Should().StartWith("## Legal Documents");
        entry.Glossary.Should().StartWith("## Legal Documents Glossary");
    }
}
