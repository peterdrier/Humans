using AwesomeAssertions;
using Humans.Base.Interfaces;

namespace Humans.Auth.Tests;

public class SectionHelpTests
{
    [HumansFact]
    public void Reads_the_RoleAssignments_help_out_of_the_assembly()
    {
        var entry = ((ISectionHelp)new Section()).HelpEntries
            .Single(e => string.Equals(e.Key, "RoleAssignments", StringComparison.Ordinal));

        entry.Guide.Should().StartWith("## Role Assignments");
        entry.Glossary.Should().StartWith("## Role Assignments Glossary");
    }
}
