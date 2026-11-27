using AwesomeAssertions;
using Humans.Base.Interfaces;

namespace Humans.Auth.Tests;

public class SectionHelpTests
{
    [HumansFact]
    public void Help_ReadsRoleAssignmentsFromAssembly()
    {
        var entry = ((ISectionHelp)new Section()).HelpEntries
            .Single(e => string.Equals(e.Key, "SystemRoles", StringComparison.Ordinal));

        entry.Guide.Should().StartWith("## System Roles");
        entry.Glossary.Should().StartWith("## System Roles Glossary");
    }
}
