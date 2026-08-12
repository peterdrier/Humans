using AwesomeAssertions;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Teams;
using Humans.Guide.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

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
    public void OnlySectionIsPublic()
    {
        // "Public means Section or Contracts/" (design §15 step 5), enforced at build time by
        // HUM0034. Guide has no <Section>Resource — its four views carry no Localizer[…] call
        // and SharedResource has no Guide_* key — and Contracts/ is an empty folder, because
        // nothing outside the section reads a guide page. No migrations either: no tables.
        var publicTypes = typeof(Section).Assembly.GetExportedTypes()
            .Select(t => t.FullName)
            .Order(StringComparer.Ordinal)
            .ToList();

        publicTypes.Should().BeEquivalentTo(["Humans.Guide.Section"]);
    }

    [HumansFact]
    public void SectionControllersAreInternal()
    {
        // Shell registers SectionControllerFeatureProvider, which relaxes MVC's IsPublic check
        // for assemblies carrying [assembly: Section("…")]
        // (memory/architecture/section-controllers-need-feature-provider.md — which says in as
        // many words: do not "fix" a 404 by making the controller public).
        var controllers = typeof(Section).Assembly.GetTypes()
            .Where(t => t.Name.EndsWith("Controller", StringComparison.Ordinal))
            .ToList();

        controllers.Should().HaveCount(1);
        controllers.Should().OnlyContain(t => !t.IsPublic);
    }

    [HumansFact]
    public void SectionTypesTakeNoStringLocalizer()
    {
        // Guide ships no resource set at all (§15 step 3b, Gate's shape). The structural guard
        // is what makes the day someone adds copy a build failure instead of a silent resolve
        // against the ambient shared set — which is exactly how Consent shipped five raw keys
        // past a green 5,000-test suite.
        var offenders = typeof(Section).Assembly.GetTypes()
            .SelectMany(t => t.GetConstructors()
                .SelectMany(c => c.GetParameters())
                .Where(p => p.ParameterType.IsGenericType
                            && p.ParameterType.GetGenericTypeDefinition() == typeof(IStringLocalizer<>))
                .Select(_ => t.FullName ?? t.Name))
            .Order(StringComparer.Ordinal)
            .ToList();

        offenders.Should().BeEmpty(
            because: "Guide has no resource set; adding localized copy means carving one first");
    }

    [HumansFact]
    public void SectionTypesTakeNoDbContext()
    {
        // Guide owns no tables. Restates, on the constructors, what the pre-move
        // "typeof(GuideContentService).Assembly does not reference EntityFrameworkCore"
        // assertion was reaching for — that form is simply false for a section assembly and
        // keeps passing while asserting nothing (§15 step 11, Calendar's rule). Guide never
        // carried it, so this is the assertion it should have had.
        var offenders = typeof(Section).Assembly.GetTypes()
            .SelectMany(t => t.GetConstructors()
                .SelectMany(c => c.GetParameters())
                .Where(p => typeof(DbContext).IsAssignableFrom(p.ParameterType)
                            || (p.ParameterType.IsGenericType
                                && p.ParameterType.GetGenericTypeDefinition() == typeof(IDbContextFactory<>)))
                .Select(_ => t.FullName ?? t.Name))
            .Order(StringComparer.Ordinal)
            .ToList();

        offenders.Should().BeEmpty(
            because: "Guide serves markdown from GitHub and owns no tables");
    }

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
            .Should().Be("Humans.Application");

        typeof(Section).Assembly.GetTypes()
            .Should().NotContain(t => t.Name == "IGuideContentSource");
    }
}
