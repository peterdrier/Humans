using AwesomeAssertions;
using Humans.Application.Interfaces;
using Humans.Teams.Contracts;
using Humans.Guide.Services;

namespace Humans.Guide.Tests;

/// <summary>
/// Architecture tests enforcing the section shape for Guide
/// (nobodies-collective/Humans#866, G5).
/// </summary>
/// <remarks>
/// Guide had no architecture test file before the move — <c>docs/sections/Guide.md</c>'s
/// touch-and-clean guidance asked for one at migration time, and this is it.
/// </remarks>
public class GuideArchitectureTests
{
    [HumansFact]
    public void RoleResolverReadsTeamsViaTheReadInterface()
    {
        var paramTypes = typeof(GuideRoleResolver).GetConstructors().Single()
            .GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(ITeamServiceRead));
        paramTypes.Should().NotContain(typeof(ITeamService),
            because: "cross-section team reads must use the read interface (section-read-write-split / HUM0032)");
    }

    [HumansFact]
    public void ContentSourceStaysABaseAbstraction()
    {
        // IGuideContentSource carries the section's name and is not the section's: its
        // signatures name only string, and three of its four consumers are elsewhere (the
        // Agent section's three preload readers, Shell's AgentDocsHealthCheck, and Base's
        // GitHubCommunityKbContentSource). Pinning the namespace here is what stops a later
        // pass "tidying" it into Humans.Guide and forcing Base to reference a section.
        typeof(IGuideContentSource).Assembly.GetName().Name
            .Should().Be("Humans.Interfaces");

        typeof(Section).Assembly.GetTypes()
            .Should().NotContain(t => t.Name == "IGuideContentSource");
    }
}
