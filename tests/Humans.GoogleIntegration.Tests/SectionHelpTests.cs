using AwesomeAssertions;
using Humans.Base.Interfaces;

namespace Humans.GoogleIntegration.Tests;

public class SectionHelpTests
{
    [HumansFact]
    public void Reads_the_GoogleSync_help_out_of_the_assembly()
    {
        var entry = ((ISectionHelp)new Section()).HelpEntries
            .Single(e => string.Equals(e.Key, "GoogleSync", StringComparison.Ordinal));

        entry.Guide.Should().StartWith("## Google Sync");
        entry.Glossary.Should().StartWith("## Google Sync Glossary");
    }
}
