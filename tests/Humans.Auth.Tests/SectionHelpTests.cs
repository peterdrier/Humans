using AwesomeAssertions;
using Humans.Base.Interfaces;

namespace Humans.Auth.Tests;

public class SectionHelpTests
{
    [HumansFact]
    public void Reads_the_RoleAssignments_help_out_of_the_assembly()
    {
        var entry = ((ISectionHelp)new Section()).HelpEntries
            .Single(e => string.Equals(e.Key, "SystemRoles", StringComparison.Ordinal));

        entry.Guide.Should().StartWith("## System Roles");
        entry.Glossary.Should().StartWith("## System Roles Glossary");
    }
}
